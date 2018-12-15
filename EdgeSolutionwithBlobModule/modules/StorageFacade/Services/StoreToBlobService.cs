namespace StorageFacade.Services
{
    using System;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Runtime.Loader;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Collections.Specialized;
    using Microsoft.Azure.Devices.Client;
    using System.Collections.Generic;
    using Microsoft.Azure.Devices.Shared;
    using Newtonsoft.Json;
    using Microsoft.WindowsAzure.Storage;
    using Microsoft.WindowsAzure.Storage.Blob;
    using Microsoft.AspNetCore.SignalR.Client;
    using StorageFacade.Models;

    public class StoreToBlobService : IServicesOnEdge
    {
        const string temperatureContainer = "temperature";
        const string anomalyContainer = "anomaly";
        const int temperatureThreshold = 25;
        const string storageConnectionString = @"DefaultEndpointsProtocol=https;BlobEndpoint=http://blob:11002/adminmg;AccountName=adminmg;AccountKey=3Q7/WEojjmagYSGUThRQew85lfPQEi0yiGMy2QtWxv6MmtYiEgb16cDLZFDUZU6t76bzU/jD57oNtnUeqTv0VQ==";
        private IDictionary<string, bool> ContainersCreated = new Dictionary<string, bool>(
            new List<KeyValuePair<string, bool>>
            {
                new KeyValuePair<string, bool>(temperatureContainer, false),
                new KeyValuePair<string, bool>(anomalyContainer, false)
            });
        private int counter;
        private HubConnection hubConnection;

        private ModuleClient _moduleClient;

        public StoreToBlobService(ModuleClient moduleClient)
        {
            _moduleClient = moduleClient;
            hubConnection = new HubConnectionBuilder()
                .WithUrl("http://mvconedge:80/sensor")
                .Build();
            hubConnection.StartAsync().Wait();
        }

        public async Task RegisterInputMessageHandlers()
        {
            await _moduleClient.SetInputMessageHandlerAsync("inputfortempsensor", ProcessMessageFromSensor, _moduleClient);
            await _moduleClient.SetInputMessageHandlerAsync("inputforml", ProcessMessageFromML, _moduleClient);
        }

        public async Task RegisterMethodHandlers()
        {
        }

        public void RunBackgroundTask()
        {
        }

        private async Task<MessageResponse> ProcessMessageFromSensor(Message message, object userContext)
        {
            await SendToSignalrHub(message);
            return await StoreMessage(message, userContext, temperatureContainer);
        }

        private async Task<MessageResponse> ProcessMessageFromML(Message message, object userContext)
        {
            return await StoreMessage(message, userContext, anomalyContainer);
        }

        private async Task SendToSignalrHub(Message message)
        {
            var messageBytes = message.GetBytes();
            var messageString = Encoding.UTF8.GetString(messageBytes);
            if (!string.IsNullOrEmpty(messageString))
            {
                var messageBody = JsonConvert.DeserializeObject<MessageBody>(messageString);
                SignalrMessage signalrMessage = new SignalrMessage 
                {
                    machine_temperature = messageBody.machine.temperature,
                    ambient_temperature = messageBody.ambient.temperature
                };
                await hubConnection.InvokeAsync("Broadcast", "tempSensor", signalrMessage);
            }
        }

        private async Task<MessageResponse> StoreMessage(Message message, object userContext, string container)
        {
            var counterValue = Interlocked.Increment(ref counter);
            try
            {
                ModuleClient moduleClient = (ModuleClient)userContext;
                var messageBytes = message.GetBytes();
                var messageString = Encoding.UTF8.GetString(messageBytes);
                Console.WriteLine($"Received message {counterValue}: [{messageString}]");
                await StoreMesssageToBlob(messageString, container);
                return MessageResponse.Completed;
            }
            catch (AggregateException ex)
            {
                foreach (Exception exception in ex.InnerExceptions)
                {
                    Console.WriteLine();
                    Console.WriteLine("Error in sample: {0}", exception);
                }
                // Indicate that the message treatment is not completed.
                var moduleClient = (ModuleClient)userContext;
                return MessageResponse.Abandoned;
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("Error in sample: {0}", ex.Message);
                // Indicate that the message treatment is not completed.
                ModuleClient moduleClient = (ModuleClient)userContext;
                return MessageResponse.Abandoned;
            }
        }
        private async Task StoreMesssageToBlob(string message, string container)
        {
            CloudStorageAccount storageAccount = null;
            CloudBlobContainer cloudBlobContainer = null;
            // Check whether the connection string can be parsed.
            if (CloudStorageAccount.TryParse(storageConnectionString, out storageAccount))
            {
                try
                {
                    CloudBlobClient cloudBlobClient = storageAccount.CreateCloudBlobClient();
                    cloudBlobContainer = cloudBlobClient.GetContainerReference(container);
                    if (!ContainersCreated[container])
                    {
                        if (!(await cloudBlobContainer.ExistsAsync()))
                            await cloudBlobContainer.CreateIfNotExistsAsync();
                        ContainersCreated[container] = true;
                    }
                    CloudBlockBlob cloudBlockBlob = cloudBlobContainer.GetBlockBlobReference(DateTime.UtcNow.ToString("yy.mm.dd.hh.mm.ss.ffffff"));
                    await cloudBlockBlob.UploadTextAsync(message);
                }
                catch (StorageException ex)
                {
                    Console.WriteLine($"Error returned from the service: {ex}");
                }
            }
        }
    }
}