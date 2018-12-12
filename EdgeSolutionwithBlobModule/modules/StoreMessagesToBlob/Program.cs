namespace StoreMessagesToBlob
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

    class Program
    {
        const string temperatureContainer = "temperature";
        const string anomalyContainer = "anomaly";
        static int counter;
        static int temperatureThreshold { get; set; } = 25;
        static string storageConnectionString = @"DefaultEndpointsProtocol=https;BlobEndpoint=http://blob:11002/adminmg;AccountName=adminmg;AccountKey=3Q7/WEojjmagYSGUThRQew85lfPQEi0yiGMy2QtWxv6MmtYiEgb16cDLZFDUZU6t76bzU/jD57oNtnUeqTv0VQ==";
        static IDictionary<string, bool> ContainersCreated = new Dictionary<string, bool>(
            new List<KeyValuePair<string, bool>>
            { 
                new KeyValuePair<string, bool>(temperatureContainer, false),
                new KeyValuePair<string, bool>(anomalyContainer, false)
            });

        static void Main(string[] args)
        {
            Init().Wait();

            // Wait until the app unloads or is cancelled
            var cts = new CancellationTokenSource();
            AssemblyLoadContext.Default.Unloading += (ctx) => cts.Cancel();
            Console.CancelKeyPress += (sender, cpe) => cts.Cancel();
            WhenCancelled(cts.Token).Wait();
        }

        /// <summary>
        /// Handles cleanup operations when app is cancelled or unloads
        /// </summary>
        public static Task WhenCancelled(CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>();
            cancellationToken.Register(s => ((TaskCompletionSource<bool>)s).SetResult(true), tcs);
            return tcs.Task;
        }

        /// <summary>
        /// Initializes the ModuleClient and sets up the callback to receive
        /// messages containing temperature information
        /// </summary>
        static async Task Init()
        {
            AmqpTransportSettings amqpSetting = new AmqpTransportSettings(TransportType.Amqp_Tcp_Only);
            ITransportSettings[] settings = { amqpSetting };

            // Open a connection to the Edge runtime
            ModuleClient ioTHubModuleClient = await ModuleClient.CreateFromEnvironmentAsync(settings);
            await ioTHubModuleClient.OpenAsync();
            Console.WriteLine("IoT Hub module client initialized.");

            // Register a callback for messages that are received by the module.
            // await ioTHubModuleClient.SetInputMessageHandlerAsync("input1", PipeMessage, iotHubModuleClient);

            // Read the TemperatureThreshold value from the module twin's desired properties
            var moduleTwin = await ioTHubModuleClient.GetTwinAsync();
            var moduleTwinCollection = moduleTwin.Properties.Desired;
            try
            {
                temperatureThreshold = moduleTwinCollection["TemperatureThreshold"];
            }
            catch (ArgumentOutOfRangeException e)
            {
                Console.WriteLine($"Property TemperatureThreshold not exist: {e.Message}");
            }
            Console.WriteLine($" TemperatureThreshold: {temperatureThreshold}");

            // Attach a callback for updates to the module twin's desired properties.
            await ioTHubModuleClient.SetDesiredPropertyUpdateCallbackAsync(OnDesiredPropertiesUpdate, null);

            // Register a callback for messages that are received by the module.
            await ioTHubModuleClient.SetInputMessageHandlerAsync("inputfortempsensor", ProcessMessageFromSensor, ioTHubModuleClient);
            await ioTHubModuleClient.SetInputMessageHandlerAsync("inputforml", ProcessMessageFromML, ioTHubModuleClient);
        }

        static Task OnDesiredPropertiesUpdate(TwinCollection desiredProperties, object userContext)
        {
            try
            {
                Console.WriteLine("Desired property change:");
                Console.WriteLine(JsonConvert.SerializeObject(desiredProperties));

                if (desiredProperties["TemperatureThreshold"] != null)
                    temperatureThreshold = desiredProperties["TemperatureThreshold"];

            }
            catch (AggregateException ex)
            {
                foreach (Exception exception in ex.InnerExceptions)
                {
                    Console.WriteLine();
                    Console.WriteLine("Error when receiving desired property: {0}", exception);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("Error when receiving desired property: {0}", ex.Message);
            }
            return Task.CompletedTask;
        }

        static async Task StoreMesssageToBlob(string message, string container)
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

        static async Task<MessageResponse> ProcessMessageFromSensor(Message message, object userContext)
        {
            return await StoreMessage(message, userContext, temperatureContainer);
        }

        static async Task<MessageResponse> ProcessMessageFromML(Message message, object userContext)
        {
            return await StoreMessage(message, userContext, anomalyContainer);
        }

        static async Task<MessageResponse> StoreMessage(Message message, object userContext, string container)
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
    }


    class MessageBody
    {
        public Machine machine { get; set; }
        public Ambient ambient { get; set; }
        public string timeCreated { get; set; }
    }
    class Machine
    {
        public double temperature { get; set; }
        public double pressure { get; set; }
    }
    class Ambient
    {
        public double temperature { get; set; }
        public int humidity { get; set; }
    }
}
