namespace StorageFacade.Services
{
    using Microsoft.Azure.Devices.Client;
    using Microsoft.WindowsAzure.Storage;
    using Microsoft.WindowsAzure.Storage.Blob;
    using MongoDB.Bson;
    using MongoDB.Driver;
    using Newtonsoft.Json;
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using StorageFacade.Models;

    public class UpstreamService : IServicesOnEdge
    {
        private const string temperatureContainer = "temperature";
        private const string anomalyContainer = "anomaly";
        private const string mongoCollectionName = "enrichedTempSensorData";
        private const string dbName = "tempSensorData";
        private const int IntervalForCommands = 5000;
        private const string storageConnectionString = @"DefaultEndpointsProtocol=https;BlobEndpoint=http://blob:11002/adminmg;AccountName=adminmg;AccountKey=3Q7/WEojjmagYSGUThRQew85lfPQEi0yiGMy2QtWxv6MmtYiEgb16cDLZFDUZU6t76bzU/jD57oNtnUeqTv0VQ==";
        private const string mongoConnectionString = "mongodb://mongodbmodule:27017";
        private int counter;
        private volatile bool shouldUpstream = false;
        private UpstreamSettings _upstreamSettings;
        private int TodayMessagesTotal = 0;
        private long TodayMessagesSizeInBytes = 0;
        private DateTime CurrentLimitsDay = DateTime.UtcNow;
        private ModuleClient _moduleClient;

        public UpstreamService(ModuleClient moduleClient)
        {
            _moduleClient = moduleClient;
            _upstreamSettings = new UpstreamSettings
            {
                TotalMessagesLimit = 100,
                TotalSizeInKbLimit = 100,
                AnomalyPriority = 1,
                TemperaturePriority = 2,
                MirthPriority = 3
            };
        }

        public async Task RegisterInputMessageHandlers()
        {
            await _moduleClient.SetInputMessageHandlerAsync("command", ExecuteCommand, null);
        }

        public async Task RegisterMethodHandlers()
        {
            await _moduleClient.SetMethodHandlerAsync("StartUpstream", StartUpstream, null);
            await _moduleClient.SetMethodHandlerAsync("StopUpstream", StopUpstream, null);
            await _moduleClient.SetMethodHandlerAsync("CleanBlobTemperature", CleanBlobTemperature, null);
            await _moduleClient.SetMethodHandlerAsync("CleanBlobAnomaly", CleanBlobAnomaly, null);
        }

        public void RunBackgroundTask()
        {
            Upstream(_moduleClient);
        }

        private async Task<MethodResponse> CleanBlobTemperature(MethodRequest request, object userContext)
        {
            return await CleanBlob(temperatureContainer);
        }

        private async Task<MethodResponse> CleanBlobAnomaly(MethodRequest request, object userContext)
        {
            return await CleanBlob(anomalyContainer);
        }

        private async Task<MethodResponse> CleanBlob(string container)
        {
            var response = new MethodResponse((int)HttpStatusCode.OK);
            Console.WriteLine("Received CleanBlob command via direct method invocation");
            shouldUpstream = false;
            CloudStorageAccount storageAccount = null;
            CloudBlobContainer cloudBlobContainer = null;
            // Check whether the connection string can be parsed.
            if (CloudStorageAccount.TryParse(storageConnectionString, out storageAccount))
            {
                try
                {
                    CloudBlobClient cloudBlobClient = storageAccount.CreateCloudBlobClient();
                    cloudBlobContainer = cloudBlobClient.GetContainerReference(container);
                    if (!(await cloudBlobContainer.ExistsAsync()))
                        await cloudBlobContainer.CreateIfNotExistsAsync();
                    else
                    {
                        await cloudBlobContainer.DeleteIfExistsAsync();
                        if (!(await cloudBlobContainer.ExistsAsync()))
                            await cloudBlobContainer.CreateIfNotExistsAsync();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Unexpected Exception {ex.Message}");
                    Console.WriteLine($"\t{ex.ToString()}");
                    response = new MethodResponse((int)HttpStatusCode.InternalServerError);
                }
            }
            return response;
        }

        private Task<MethodResponse> StartUpstream(MethodRequest request, object userContext)
        {
            var response = new MethodResponse((int)HttpStatusCode.OK);
            Console.WriteLine($"Received StartUpstream command: {request.DataAsJson}");
            try
            {
                _upstreamSettings = JsonConvert.DeserializeObject<UpstreamSettings>(request.DataAsJson);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to deserialize control command with exception: [{ex.Message}]");
            }
            shouldUpstream = true;
            return Task.FromResult(response);
        }

        private Task<MethodResponse> StopUpstream(MethodRequest request, object userContext)
        {
            var response = new MethodResponse((int)HttpStatusCode.OK);
            Console.WriteLine("Received StopUpstream command via direct method invocation");
            shouldUpstream = false;
            return Task.FromResult(response);
        }

        private async Task Upstream(ModuleClient moduleClient)
        {
            while (true)
            {
                try
                {
                    if (shouldUpstream)
                    {
                        var upstreamMethods = new SortedDictionary<int, FuncWithArgs>();
                        upstreamMethods.Add(_upstreamSettings.AnomalyPriority, new FuncWithArgs
                        {
                            Func = UpstreamFromBlob,
                            Container = anomalyContainer,
                            ModuleClient = moduleClient
                        });
                        upstreamMethods.Add(_upstreamSettings.TemperaturePriority, new FuncWithArgs
                        {
                            Func = UpstreamFromBlob,
                            Container = temperatureContainer,
                            ModuleClient = moduleClient
                        });
                        upstreamMethods.Add(_upstreamSettings.MirthPriority, new FuncWithArgs
                        {
                            Func = UpstreamFromMongo,
                            Container = mongoCollectionName,
                            ModuleClient = moduleClient
                        });
                        foreach (var method in upstreamMethods)
                        {
                            await method.Value.Func(method.Value.Container, method.Value.ModuleClient);
                        }
                        shouldUpstream = false;
                    }
                    else
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(IntervalForCommands));
                        Console.WriteLine("Silent mode");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Unexpected Exception {ex.Message}");
                    Console.WriteLine($"\t{ex.ToString()}");
                }
            }
        }

        private bool IsLimitsReached(byte[] message)
        {
            if (CurrentLimitsDay.Date != DateTime.UtcNow.Date)
            {
                CurrentLimitsDay = DateTime.UtcNow;
                TodayMessagesTotal = 0;
                TodayMessagesSizeInBytes = 0;
            }
            if (TodayMessagesTotal + 1 < _upstreamSettings.TotalMessagesLimit)
            {
                TodayMessagesTotal++;
            }
            else
            {
                Console.WriteLine($"Limit by total messages has been reached: {TodayMessagesTotal + 1}");
                return true;
            }
            if (TodayMessagesSizeInBytes + message.LongLength < _upstreamSettings.TotalSizeInKbLimit * 1024)
            {
                TodayMessagesSizeInBytes += message.LongLength;
            }
            else
            {
                Console.WriteLine($"Limit by messages size has been reached: {TodayMessagesSizeInBytes}");
                return true;
            }
            return false;
        }

        private IMongoCollection<T> GetCollection<T>(MongoClient client) where T : class
        {
            var database = client.GetDatabase(dbName);
            return database.GetCollection<T>(mongoCollectionName);
        }
        private async Task UpstreamFromMongo(string container, ModuleClient moduleClient)
        {
            Console.WriteLine("***");
            Console.WriteLine($"Upstream from Mongo: {container}");
            int count = 1;
            MongoClient client = new MongoClient(mongoConnectionString);
            try
            {
                var collection = GetCollection<MessageBody>(client);
                var all = collection.Find(FilterDefinition<MessageBody>.Empty);

                foreach (var document in all.ToList())
                {
                    if (!shouldUpstream)
                        break;

                    var body = document.ToJson();
                    var messageBytes = Encoding.UTF8.GetBytes(body);

                    if (IsLimitsReached(messageBytes))
                    {
                        shouldUpstream = false;
                    }
                    else
                    {
                        var message = new Message(messageBytes);
                        message.ContentEncoding = "utf-8";
                        message.ContentType = "application/json";
                        await moduleClient.SendEventAsync("output1", message);
                        Console.WriteLine($"Message sent {count}: {body} from Mongo DB");
                        count++;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error returned from the UpstreamFromMongo: {ex}");
            }
        }

        private async Task UpstreamFromBlob(string container, ModuleClient moduleClient)
        {
            Console.WriteLine("***");
            Console.WriteLine($"Upstream from Blob: {container}");
            int count = 1;
            CloudStorageAccount storageAccount = null;
            CloudBlobContainer cloudBlobContainer = null;
            // Check whether the connection string can be parsed.
            if (CloudStorageAccount.TryParse(storageConnectionString, out storageAccount))
            {
                try
                {
                    CloudBlobClient cloudBlobClient = storageAccount.CreateCloudBlobClient();
                    cloudBlobContainer = cloudBlobClient.GetContainerReference(container);
                    if (!(await cloudBlobContainer.ExistsAsync()))
                        await cloudBlobContainer.CreateIfNotExistsAsync();
                    BlobContinuationToken blobContinuationToken = null;
                    do
                    {
                        var results = await cloudBlobContainer.ListBlobsSegmentedAsync(null, blobContinuationToken);
                        // Get the value of the continuation token returned by the listing call.
                        blobContinuationToken = results.ContinuationToken;
                        foreach (IListBlobItem item in results.Results)
                        {
                            if (!shouldUpstream)
                                break;
                            if (item.GetType() == typeof(CloudBlockBlob))
                            {
                                CloudBlockBlob blob = (CloudBlockBlob)item;
                                Console.WriteLine(await blob.DownloadTextAsync());
                                var body = await blob.DownloadTextAsync();
                                if (!string.IsNullOrEmpty(body))
                                {
                                    var messageBytes = Encoding.UTF8.GetBytes(body);
                                    if (IsLimitsReached(messageBytes))
                                    {
                                        shouldUpstream = false;
                                    }
                                    else
                                    {
                                        var message = new Message(messageBytes);
                                        message.ContentEncoding = "utf-8";
                                        message.ContentType = "application/json";
                                        await moduleClient.SendEventAsync("output1", message);
                                        Console.WriteLine($"Message sent {count}: {body}");
                                        count++;
                                    }
                                }
                            }
                        }
                    } while (blobContinuationToken != null && shouldUpstream);
                }
                catch (StorageException ex)
                {
                    Console.WriteLine($"Error returned from the service: {ex}");
                }
            }
        }

        /// <summary>
        /// This method is called whenever the module is sent a message from the EdgeHub. 
        /// It just pipe the messages without any change.
        /// It prints all the incoming messages.
        /// </summary>
        private Task<MessageResponse> ExecuteCommand(Message message, object userContext)
        {
            Console.WriteLine("ExecuteCommand");
            int counterValue = Interlocked.Increment(ref counter);

            var moduleClient = userContext as ModuleClient;
            if (moduleClient == null)
            {
                throw new InvalidOperationException("UserContext doesn't contain " + "expected values");
            }

            byte[] messageBytes = message.GetBytes();
            string messageString = Encoding.UTF8.GetString(messageBytes);
            Console.WriteLine($"Received command: {counterValue}, Body: [{messageString}]");

            if (!string.IsNullOrEmpty(messageString))
            {
                foreach (var prop in message.Properties)
                {
                    if (prop.Key.ToLower().Equals("upstream"))
                    {
                        switch (prop.Value.ToLower())
                        {
                            case "start":
                                shouldUpstream = true;
                                Console.WriteLine("Start upstream");
                                break;
                            case "stop":
                                shouldUpstream = false;
                                Console.WriteLine("Stop upstream");
                                break;
                            default:
                                Console.WriteLine($"Upstream command option {prop.Value} is not supported");
                                break;
                        }
                    }
                    else
                        Console.WriteLine($"Command {prop.Key} is not supported");
                }
            }
            return Task.FromResult(MessageResponse.Completed);
        }
        class FuncWithArgs
        {
            public Func<string, ModuleClient, Task> Func { get; set; }
            public string Container { get; set; }
            public ModuleClient ModuleClient { get; set; }
        }
    }
}