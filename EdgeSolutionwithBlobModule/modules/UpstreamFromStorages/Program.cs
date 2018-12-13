namespace UpstreamFromStorages
{
    using System;
    using System.Net;
    using System.IO;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;
    using System.Runtime.Loader;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Devices.Client;
    using Newtonsoft.Json;
    using Microsoft.WindowsAzure.Storage;
    using Microsoft.WindowsAzure.Storage.Blob;
    using MongoDB.Bson;
    using MongoDB.Driver;

    class Program
    {
        const string temperatureContainer = "temperature";
        const string anomalyContainer = "anomaly";
        const string mongoCollectionName = "enrichedTempSensorData";
        static int counter;
        static volatile bool shouldUpstream = false;
        static int IntervalForCommands = 5000;
        static UpstreamSettings upstreamSettings = new UpstreamSettings();
        static int TodayMessagesTotal = 0;
        static long TodayMessagesSizeInBytes = 0;
        static DateTime CurrentLimitsDay = DateTime.UtcNow;
        static string storageConnectionString = @"DefaultEndpointsProtocol=https;BlobEndpoint=http://blob:11002/adminmg;AccountName=adminmg;AccountKey=3Q7/WEojjmagYSGUThRQew85lfPQEi0yiGMy2QtWxv6MmtYiEgb16cDLZFDUZU6t76bzU/jD57oNtnUeqTv0VQ==";
        static string mongoConnectionString = "mongodb://mongodbmodule:27017";
        private static string dbName = "tempSensorData";

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
            upstreamSettings.InitDefaults();
            AmqpTransportSettings amqpSetting = new AmqpTransportSettings(TransportType.Amqp_Tcp_Only);
            ITransportSettings[] settings = { amqpSetting };

            // Open a connection to the Edge runtime
            ModuleClient ioTHubModuleClient = await ModuleClient.CreateFromEnvironmentAsync(settings);
            await ioTHubModuleClient.OpenAsync();
            Console.WriteLine("IoT Hub module client initialized.");

            // Register callback to be called when a message is received by the module
            await ioTHubModuleClient.SetInputMessageHandlerAsync("command", ExecuteCommand, null);

            await ioTHubModuleClient.SetMethodHandlerAsync("StartUpstream", StartUpstream, null);
            await ioTHubModuleClient.SetMethodHandlerAsync("StopUpstream", StopUpstream, null);
            await ioTHubModuleClient.SetMethodHandlerAsync("CleanBlobTemperature", CleanBlobTemperature, null);
            await ioTHubModuleClient.SetMethodHandlerAsync("CleanBlobAnomaly", CleanBlobAnomaly, null);

            Upstream(ioTHubModuleClient);
        }

        private static async Task<MethodResponse> CleanBlobTemperature(MethodRequest request, object userContext)
        {
            return await CleanBlob(temperatureContainer);
        }

        private static async Task<MethodResponse> CleanBlobAnomaly(MethodRequest request, object userContext)
        {
            return await CleanBlob(anomalyContainer);
        }

        private static async Task<MethodResponse> CleanBlob(string container)
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

        private static Task<MethodResponse> StartUpstream(MethodRequest request, object userContext)
        {
            var response = new MethodResponse((int)HttpStatusCode.OK);
            Console.WriteLine($"Received StartUpstream command: {request.DataAsJson}");
            try
            {
                upstreamSettings = JsonConvert.DeserializeObject<UpstreamSettings>(request.DataAsJson);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to deserialize control command with exception: [{ex.Message}]");
            }
            shouldUpstream = true;
            return Task.FromResult(response);
        }

        private static Task<MethodResponse> StopUpstream(MethodRequest request, object userContext)
        {
            var response = new MethodResponse((int)HttpStatusCode.OK);
            Console.WriteLine("Received StopUpstream command via direct method invocation");
            shouldUpstream = false;
            return Task.FromResult(response);
        }

        static async Task Upstream(ModuleClient ioTHubModuleClient)
        {
            while (true)
            {
                try
                {
                    if (shouldUpstream)
                    {
                        var upstreamMethods = new SortedDictionary<int, FuncWithArgs>();
                        upstreamMethods.Add(upstreamSettings.AnomalyPriority, new FuncWithArgs
                        {
                            Func = UpstreamFromBlob,
                            Container = anomalyContainer,
                            ModuleClient = ioTHubModuleClient
                        });
                        upstreamMethods.Add(upstreamSettings.TemperaturePriority, new FuncWithArgs
                        {
                            Func = UpstreamFromBlob,
                            Container = temperatureContainer,
                            ModuleClient = ioTHubModuleClient
                        });
                        upstreamMethods.Add(upstreamSettings.MirthPriority, new FuncWithArgs
                        {
                            Func = UpstreamFromMongo,
                            Container = mongoCollectionName,
                            ModuleClient = ioTHubModuleClient
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

        private static bool IsLimitsReached(byte[] message)
        {
            if (CurrentLimitsDay.Date != DateTime.UtcNow.Date)
            {
                CurrentLimitsDay = DateTime.UtcNow;
                TodayMessagesTotal = 0;
                TodayMessagesSizeInBytes = 0;
            }
            if (TodayMessagesTotal + 1 < upstreamSettings.TotalMessagesLimit)
            {
                TodayMessagesTotal++;
            }
            else
            {
                Console.WriteLine($"Limit by total messages has been reached: {TodayMessagesTotal + 1}");
                return true;
            }
            if (TodayMessagesSizeInBytes + message.LongLength < upstreamSettings.TotalSizeInKbLimit * 1024)
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

        private static IMongoCollection<T> GetCollection<T>(MongoClient client) where T : class
        {
            var database = client.GetDatabase(dbName);
            return database.GetCollection<T>(mongoCollectionName);
        }
        private static async Task UpstreamFromMongo(string container, ModuleClient ioTHubModuleClient)
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
                        await ioTHubModuleClient.SendEventAsync("output1", message);
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

        private static async Task UpstreamFromBlob(string container, ModuleClient ioTHubModuleClient)
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
                                        await ioTHubModuleClient.SendEventAsync("output1", message);
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
        static async Task<MessageResponse> ExecuteCommand(Message message, object userContext)
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
            return MessageResponse.Completed;
        }
    }

    public class UpstreamSettings
    {
        public int TotalMessagesLimit { get; set; }
        public int TotalSizeInKbLimit { get; set; }
        public int TemperaturePriority { get; set; }
        public int AnomalyPriority { get; set; }
        public int MirthPriority { get; set; }

        public void InitDefaults()
        {
            TotalMessagesLimit = 100;
            TotalSizeInKbLimit = 100;
            AnomalyPriority = 1;
            TemperaturePriority = 2;
            MirthPriority = 3;
        }
    }

    public class FuncWithArgs
    {
        public Func<string, ModuleClient, Task> Func { get; set; }
        public string Container { get; set; }
        public ModuleClient ModuleClient { get; set; }
    }
    class MessageBody
    {
        public ObjectId _id { get; set; }
        public Machine machine { get; set; }
        public Ambient ambient { get; set; }
        public string timeCreated { get; set; }
        public MirthInfo mirthInfo { get; set; }
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

    class MirthInfo
    {
        public string pId { get; set; }
        public string name { get; set; }
    }
}
