namespace UpstreamFromBlob
{
    using System;
    using System.Net;
    using System.IO;
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
    class Program
    {
        static int counter;
        static volatile bool shouldUpstream = false;
        static int IntervalForCommands = 5000;
        static string storageConnectionString = @"DefaultEndpointsProtocol=https;BlobEndpoint=http://blob:11002/adminmg;AccountName=adminmg;AccountKey=3Q7/WEojjmagYSGUThRQew85lfPQEi0yiGMy2QtWxv6MmtYiEgb16cDLZFDUZU6t76bzU/jD57oNtnUeqTv0VQ==";

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

            // Register callback to be called when a message is received by the module
            await ioTHubModuleClient.SetInputMessageHandlerAsync("command", ExecuteCommand, null);

            await ioTHubModuleClient.SetMethodHandlerAsync("StartUpstream", StartUpstream, null);
            await ioTHubModuleClient.SetMethodHandlerAsync("StopUpstream", StopUpstream, null);
            await ioTHubModuleClient.SetMethodHandlerAsync("CleanBlob", CleanBlob, null);

            Upstream(ioTHubModuleClient);
        }

        private static async Task<MethodResponse> CleanBlob(MethodRequest request, object userContext)
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
                    cloudBlobContainer = cloudBlobClient.GetContainerReference("iotedge");
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
            Console.WriteLine("Received StartUpstream command via direct method invocation");
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
                        int count = 1;
                        CloudStorageAccount storageAccount = null;
                        CloudBlobContainer cloudBlobContainer = null;
                        // Check whether the connection string can be parsed.
                        if (CloudStorageAccount.TryParse(storageConnectionString, out storageAccount))
                        {
                            try
                            {
                                CloudBlobClient cloudBlobClient = storageAccount.CreateCloudBlobClient();
                                cloudBlobContainer = cloudBlobClient.GetContainerReference("iotedge");
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
                                                var message = new Message(messageBytes);
                                                message.ContentEncoding = "utf-8";
                                                message.ContentType = "application/json";
                                                await ioTHubModuleClient.SendEventAsync("output1", message);
                                                Console.WriteLine($"Message sent {count}: {body}");
                                                count++;
                                            }
                                        }
                                    }
                                    shouldUpstream = false;
                                } while (blobContinuationToken != null && shouldUpstream);
                            }
                            catch (StorageException ex)
                            {
                                Console.WriteLine($"Error returned from the service: {ex}");
                            }
                        }
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

        /// <summary>
        /// This method is called whenever the module is sent a message from the EdgeHub. 
        /// It just pipe the messages without any change.
        /// It prints all the incoming messages.
        /// </summary>
        static async Task<MessageResponse> ExecuteCommand(Message message, object userContext)
        {
            Console.WriteLine("PipeMessage");
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
                                shouldUpstream = true;
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
}
