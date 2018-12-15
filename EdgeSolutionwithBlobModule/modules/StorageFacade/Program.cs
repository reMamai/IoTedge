namespace StorageFacade
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
    using StorageFacade.Services;

    class Program
    {
        private static IServicesOnEdge upstreamService;
        private static IServicesOnEdge storeToBlobService;
        private static IServicesOnEdge storeToMongoService;
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

            upstreamService = new UpstreamService(ioTHubModuleClient);
            storeToBlobService = new StoreToBlobService(ioTHubModuleClient);
            storeToMongoService = new StoreToMongoService(ioTHubModuleClient);

            await upstreamService.RegisterInputMessageHandlers();
            await storeToBlobService.RegisterInputMessageHandlers();
            await storeToMongoService.RegisterInputMessageHandlers();

            await upstreamService.RegisterMethodHandlers();
            await storeToBlobService.RegisterMethodHandlers();
            await storeToMongoService.RegisterMethodHandlers();

            upstreamService.RunBackgroundTask();
        }
    }
}
