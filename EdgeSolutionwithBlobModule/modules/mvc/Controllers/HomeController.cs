using System;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Devices.Client;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using mvc.Models;
using Newtonsoft.Json;
using MongoDB.Bson;
using MongoDB.Driver;

namespace mvc.Controllers
{
    public class HomeController : Controller
    {
        const string temperatureContainer = "temperature";
        const string anomalyContainer = "anomaly";
        const string mongoCollectionName = "enrichedTempSensorData";
        const string mongoConnectionString = "mongodb://mongodbmodule:27017";
        const string dbName = "tempSensorData";

        private string storageConnectionString = @"DefaultEndpointsProtocol=https;BlobEndpoint=http://blob:11002/adminmg;AccountName=adminmg;AccountKey=3Q7/WEojjmagYSGUThRQew85lfPQEi0yiGMy2QtWxv6MmtYiEgb16cDLZFDUZU6t76bzU/jD57oNtnUeqTv0VQ==";
        private ModuleClient ioTHubModuleClient = null;
        private string deviceId;
        private string moduleUpstreamFromStorages = "UpstreamFromStorages";
        private static UpstreamSettings settings = new UpstreamSettings
        {
            TotalMessagesLimit = 100,
            TotalSizeInKbLimit = 100,
            AnomalyPriority = 1,
            TemperaturePriority = 2,
            MirthPriority = 3
        };
        public HomeController()
        {            
            Init().Wait();
        }

        private async Task Init()
        {
            AmqpTransportSettings amqpSetting = new AmqpTransportSettings(Microsoft.Azure.Devices.Client.TransportType.Amqp_Tcp_Only);
            ITransportSettings[] settings = { amqpSetting };
            ioTHubModuleClient = await ModuleClient.CreateFromEnvironmentAsync(settings);
            await ioTHubModuleClient.OpenAsync();
            // Get deviced id of this device, exposed as a system variable by the iot edge runtime: export IOTEDGE_DEVICEID="myEdgeDeviceInGC"
            deviceId = System.Environment.GetEnvironmentVariable("IOTEDGE_DEVICEID");
        }
        public IActionResult Index()
        {
            return View();
        }

        private async Task<StoreModel> FetchFromMongo(string container)
        {
            var model = new StoreModel { StoreItems = new List<KeyValuePair<string, string>>() };
            MongoClient client = new MongoClient(mongoConnectionString);
            var database = client.GetDatabase(dbName);            
            try
            {
                var collection = database.GetCollection<MessageBody>(mongoCollectionName);
                var all = await collection.FindAsync(FilterDefinition<MessageBody>.Empty);
                int count = 0;
                foreach (var document in all.ToList())
                {
                    if (count < 10)
                    {
                        var content = document.ToJson();
                        model.StoreItems.Add(new KeyValuePair<string, string>(document.timeCreated, content));
                    }
                    count++;
                }
                model.Total = count;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error returned from the UpstreamFromMongo: {ex}");
            }
            return model;
        }

        private async Task<StoreModel> FetchFromBlob(string container)
        {
            var model = new StoreModel { StoreItems = new List<KeyValuePair<string, string>>() };

            CloudStorageAccount storageAccount = null;
            CloudBlobContainer cloudBlobContainer = null;
            if (CloudStorageAccount.TryParse(storageConnectionString, out storageAccount))
            {
                try
                {
                    CloudBlobClient cloudBlobClient = storageAccount.CreateCloudBlobClient();
                    cloudBlobContainer = cloudBlobClient.GetContainerReference(container);
                    BlobContinuationToken blobContinuationToken = null;
                    int count = 0;
                    do
                    {
                        var results = await cloudBlobContainer.ListBlobsSegmentedAsync(null, blobContinuationToken);

                        blobContinuationToken = results.ContinuationToken;
                        foreach (IListBlobItem item in results.Results)
                        {
                            if (count < 10)
                            {
                                if (item.GetType() == typeof(CloudBlockBlob))
                                {
                                    CloudBlockBlob blob = (CloudBlockBlob)item;
                                    var content = await blob.DownloadTextAsync();
                                    model.StoreItems.Add(new KeyValuePair<string, string>(item.Uri.AbsoluteUri, content));
                                }
                            }
                            count++;
                        }
                    } while (blobContinuationToken != null);
                    model.Total = count;
                }
                catch (StorageException ex)
                {

                    Console.WriteLine("Error returned from the service: {0}", ex.Message);
                }
            }
            return model;
        }
        public async Task<IActionResult> BlobTemperature()
        {
            var model = await FetchFromBlob(temperatureContainer);
            return View(model);
        }
        public async Task<IActionResult> Mongo()
        {
            var model = await FetchFromMongo(mongoCollectionName);
            return View(model);
        }

        public async Task<IActionResult> BlobAnomaly()
        {
            var model = await FetchFromBlob(anomalyContainer);
            return View(model);
        }
        public async Task<IActionResult> Start()
        {
            var payload = JsonConvert.SerializeObject(settings);
            await ioTHubModuleClient.InvokeMethodAsync(deviceId, moduleUpstreamFromStorages, new MethodRequest("StartUpstream", Encoding.UTF8.GetBytes(payload)));
            return RedirectToAction("BlobTemperature");
        }

        public async Task<IActionResult> Stop()
        {
            await ioTHubModuleClient.InvokeMethodAsync(deviceId, moduleUpstreamFromStorages, new MethodRequest("StopUpstream", Encoding.UTF8.GetBytes("{}")));
            return RedirectToAction("BlobTemperature");
        }
        public async Task<IActionResult> CleanTemperature(string container)
        {
            await ioTHubModuleClient.InvokeMethodAsync(deviceId, moduleUpstreamFromStorages, new MethodRequest("CleanBlobTemperature", Encoding.UTF8.GetBytes("{}")));
            return RedirectToAction("BlobTemperature");
        }
        public async Task<IActionResult> CleanAnomaly(string container)
        {
            await ioTHubModuleClient.InvokeMethodAsync(deviceId, moduleUpstreamFromStorages, new MethodRequest("CleanBlobAnomaly", Encoding.UTF8.GetBytes("{}")));
            return RedirectToAction("BlobAnomaly");
        }
        public async Task<IActionResult> Reset()
        {
            await ioTHubModuleClient.InvokeMethodAsync(deviceId, "tempSensor", new MethodRequest("reset", Encoding.UTF8.GetBytes("{}")));
            return RedirectToAction("BlobTemperature");
        }

        public IActionResult Settings()
        {            
            return View(settings);
        }
        [HttpPost]
        public IActionResult Settings([Bind("AnomalyPriority, MirthPriority, TemperaturePriority, TotalMessagesLimit, TotalSizeInKbLimit")]UpstreamSettings model)
        {
            settings = model;
            return View(settings);
        }

        public IActionResult Contact()
        {
            ViewData["Message"] = "Your contact page.";

            return View();
        }

        public IActionResult Privacy()
        {
            return View();
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
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
