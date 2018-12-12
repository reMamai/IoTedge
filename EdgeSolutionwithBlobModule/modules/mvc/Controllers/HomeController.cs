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

namespace mvc.Controllers
{
    public class HomeController : Controller
    {
        private string storageConnectionString = @"DefaultEndpointsProtocol=https;BlobEndpoint=http://blob:11002/adminmg;AccountName=adminmg;AccountKey=3Q7/WEojjmagYSGUThRQew85lfPQEi0yiGMy2QtWxv6MmtYiEgb16cDLZFDUZU6t76bzU/jD57oNtnUeqTv0VQ==";
        private ModuleClient ioTHubModuleClient = null;
        private string deviceId;
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

        private async Task<BlobModel> Fetch()
        {
            var model = new BlobModel { BlobItems = new List<KeyValuePair<string, string>>() };

            CloudStorageAccount storageAccount = null;
            CloudBlobContainer cloudBlobContainer = null;
            if (CloudStorageAccount.TryParse(storageConnectionString, out storageAccount))
            {
                try
                {
                    CloudBlobClient cloudBlobClient = storageAccount.CreateCloudBlobClient();
                    cloudBlobContainer = cloudBlobClient.GetContainerReference("temperature");
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
                                    model.BlobItems.Add(new KeyValuePair<string, string>(item.Uri.AbsoluteUri, content));
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

        public async Task<IActionResult> Blob()
        {
            var model = await Fetch();
            return View(model);
        }
        public async Task<IActionResult> Start()
        {            
            await ioTHubModuleClient.InvokeMethodAsync(deviceId, "UpstreamFromBlob", new MethodRequest("StartUpstream", Encoding.UTF8.GetBytes("{}")));
            return RedirectToAction("Blob");
        }

        public async Task<IActionResult> Stop()
        {
            await ioTHubModuleClient.InvokeMethodAsync(deviceId, "UpstreamFromBlob", new MethodRequest("StopUpstream", Encoding.UTF8.GetBytes("{}")));
            return RedirectToAction("Blob");
        }
        public async Task<IActionResult> Clean()
        {
            await ioTHubModuleClient.InvokeMethodAsync(deviceId, "UpstreamFromBlob", new MethodRequest("CleanBlob", Encoding.UTF8.GetBytes("{}")));
            return RedirectToAction("Blob");
        }
        public async Task<IActionResult> Reset()
        {
            await ioTHubModuleClient.InvokeMethodAsync(deviceId, "tempSensor", new MethodRequest("reset", Encoding.UTF8.GetBytes("{}")));
            return RedirectToAction("Blob");
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
}
