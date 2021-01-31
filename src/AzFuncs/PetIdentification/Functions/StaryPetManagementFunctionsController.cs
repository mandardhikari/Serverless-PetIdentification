using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.EventGrid;
using Microsoft.Azure.EventGrid.Models;
using Microsoft.Extensions.Logging;
using PetIdentification.Interfaces;
using PetIdentification.Models;
using PetIdentification.Dtos;
using PetIdentification.Constants;
using System.Linq;
using AutoMapper;
using Newtonsoft.Json.Linq;
using Microsoft.Azure.WebJobs.Extensions.SignalRService;
using Newtonsoft.Json;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.IO;

namespace PetIdentification.Functions
{

    public class StaryPetManagementFunctionsController
    {
        #region Properties
        private readonly IAdoptionCentreDbHelper _dbHelper;
        private readonly IPredictionHelper _predictionHelper;
        private readonly IMapper _mapper;

        #endregion

        #region Constructors

        public StaryPetManagementFunctionsController
        (
            IAdoptionCentreDbHelper dbHelper,
            IPredictionHelper predictionHelper,
            IMapper mapper
        )
        {

            _dbHelper = dbHelper ?? 
            throw new ArgumentNullException(nameof(dbHelper));
            _predictionHelper = predictionHelper ?? 
            throw new ArgumentNullException(nameof(predictionHelper));

            _mapper = mapper ?? 
            throw new ArgumentNullException(nameof(mapper));
        }

        #endregion

        #region ActivityFunctions
        [FunctionName("IdentifyStrayPetBreedAsync")]
        public async Task<List<PredictionResult>> PredictStrayPetBreedAsync(
            [ActivityTrigger]string imageUrl,
            ILogger logger)
        {
            logger.LogInformation($"Started the execution of IdentifyStrayPetBreedAsync function");

            var result = await _predictionHelper.PredictBreedAsync(imageUrl);
            
            logger.LogInformation($"Finshed calling the PredictBreedAsync function from prediction helper");

            return result.ToList();;
        }

        [FunctionName("LocateAdoptionCentresByBreedAsync")]
        public async Task<List<AdoptionCentre>> LocateAdoptionCentresByBreedAsync(
            [ActivityTrigger] string breed,
            ILogger logger
        )
        {
            logger.LogInformation("Started the execution of the LocateAdoptionCentresByBreedAsync activity function");
            var result = await _dbHelper.GetAdoptionCentresByBreedAsync(breed);

            logger.LogInformation("Finished the execution of LocateAdoptionCentresByBreedAsync activity function");

            return result.ToList();

        }

        [FunctionName("PushMessagesToSignalRHub")]
        public async Task<bool> PushMessagesToSignalRHub(
            [ActivityTrigger] SignalRRequest request,
            [SignalR(HubName = SignalRConstants.HubName )]IAsyncCollector<SignalRMessage> signalRMessages,
            ILogger logger
        )
        {
            logger.LogInformation("Sending out signal R push notification");
            
            await signalRMessages.AddAsync(
                new SignalRMessage 
                {
                    // the message will only be sent to this user ID
                    UserId = request.UserId,
                    Target = "sendPetAdoptionCentres",
                    Arguments = new [] { JsonConvert.SerializeObject(request.AdoptionCentres) }
                });
                
            return true;

        }

        #endregion

        #region Orchestration
        [FunctionName("StrayPetManagementOrchestration")]
        public async Task<string> RunOrchestrator(
            [OrchestrationTrigger] IDurableOrchestrationContext context,
            ILogger logger
        )
        {
            logger.LogInformation("Starting the execution of the orchestration.");
            
            var durableReqDto = context.GetInput<DurableRequestDto>();

            //var imageUrl = context.GetInput<string>();
            
            var predictions = await context.CallActivityAsync<List<PredictionResult>>
            ("IdentifyStrayPetBreedAsync", durableReqDto.BlobUrl.ToString());

            var highestPrediction = predictions.OrderBy( x => x.Probability).FirstOrDefault();
            
            var adoptionCentres = _mapper.Map<List<AdoptionCentre>, List<AdoptionCentreDto>>(
                await context.CallActivityAsync<List<AdoptionCentre>>(
                    "LocateAdoptionCentresByBreedAsync", highestPrediction.TagName
                )
            );

            var signalRRequest = new SignalRRequest()
            {
                AdoptionCentres = adoptionCentres,
                UserId = durableReqDto.SignalRUserId
            };

            await context.CallActivityAsync("PushMessagesToSignalRHub", signalRRequest);

            logger.LogInformation("Finished execution of the orchestration");

            return "Orchestrator executed the functions.";

        }

        #endregion

        #region DurableClients

        [FunctionName("StrayPetManagementEventGridClient")]
        public async Task StrayPetManagementEventGridClient(
            [EventGridTrigger] EventGridEvent eventGridEvent,
            [DurableClient] IDurableClient client,
            ILogger logger
        )
        {
            logger.LogInformation("Started the execution of the event grid triggered durable orchestration module.");
            StorageBlobCreatedEventData blobCreatedEventData = 
                ((JObject)eventGridEvent.Data).ToObject<StorageBlobCreatedEventData>();
            
            var result = await client
            .StartNewAsync("StrayPetManagementOrchestration", instanceId: new Guid().ToString(), blobCreatedEventData.Url);

        }

        [FunctionName("StrayPetManagementDurableHttpClient")]
        public async Task<IActionResult> StrayPetManagementDurableHttpClient(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest request,
            [DurableClient] IDurableClient durableClient,
            ILogger logger
        )
        {
            logger.LogInformation("Started the execution of the event grid triggered durable function");

            var requestBody = string.Empty;

            using(StreamReader reader = new StreamReader(request.Body))
            {
                requestBody = await reader.ReadToEndAsync();
            }

            var durableReqDto = JsonConvert.DeserializeObject<DurableRequestDto>(requestBody);


            var result = await durableClient
                .StartNewAsync("StrayPetManagementOrchestration", instanceId: new Guid().ToString(), durableReqDto);
            
            return new AcceptedResult();

        }

        #endregion

    }
    
}