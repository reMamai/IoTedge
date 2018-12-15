namespace StorageFacade.Services
{
    using System;
    using System.Threading;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using System.Threading.Tasks;
    using Microsoft.Azure.Devices.Client;
    using MongoDB.Driver;
    using Newtonsoft.Json;
    using StorageFacade.Models;
    public class StoreToMongoService : IServicesOnEdge
    {
        const string connectionString = "mongodb://mongodbmodule:27017";
        const string dbName = "tempSensorData";
        const string collectionName = "enrichedTempSensorData";
        private int counter;
        private ModuleClient _moduleClient;

        public StoreToMongoService(ModuleClient moduleClient)
        {
            _moduleClient = moduleClient;
        }
        
        public async Task RegisterInputMessageHandlers()
        {
            await _moduleClient.SetInputMessageHandlerAsync("inputfortemp", StoreMessageToMongo, _moduleClient);
        }

        public async Task RegisterMethodHandlers()
        {
        }

        public void RunBackgroundTask()
        {
        }

        private async Task<MessageResponse> StoreMessageToMongo(Message message, object userContext)
        {
            var counterValue = Interlocked.Increment(ref counter);
            try
            {
                ModuleClient moduleClient = (ModuleClient)userContext;
                var messageBytes = message.GetBytes();
                var messageString = Encoding.UTF8.GetString(messageBytes);
                if (!string.IsNullOrEmpty(messageString))
                {
                    Console.WriteLine($"Received message {counterValue}: [{messageString}]");
                    var messageBody = JsonConvert.DeserializeObject<MessageBody>(messageString);
                    messageBody = EnrichMessage(messageBody);
                    SaveToDb(messageBody);
                }
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
                Console.WriteLine("Error in StoreMessageToMongo: {0}", ex.Message);
                // Indicate that the message treatment is not completed.
                ModuleClient moduleClient = (ModuleClient)userContext;
                return MessageResponse.Abandoned;
            }
        }

        private MessageBody EnrichMessage(MessageBody messag)
        {
            messag.mirthInfo = new MirthInfo { pId = Guid.NewGuid().ToString(), name = "John Doe" };
            return messag;
        }

        private void SaveToDb(MessageBody message)
        {
            MongoClient mng = new MongoClient(connectionString);
            var db = mng.GetDatabase(dbName);
            var collection = db.GetCollection<MessageBody>(collectionName);
            collection.InsertOne(message);
        }
    }
}