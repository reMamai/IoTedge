using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.Devices.Client;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.EdgeHub;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Newtonsoft.Json;

namespace Functions.Samples
{
    public static class StoreMessagesToMongo
    {
        private static string connectionString = "mongodb://mongodbmodule:27017";
        private static string dbName = "tempSensorData";
        private static string collectionName = "enrichedTempSensorData";

        [FunctionName("StoreMessagesToMongo")]
        public static void FilterMessageAndSendMessage(
                    [EdgeHubTrigger("input1")] Message messageReceived,
                    ILogger logger)
        {
            try
            {
                byte[] messageBytes = messageReceived.GetBytes();
                var messageString = System.Text.Encoding.UTF8.GetString(messageBytes);


                if (!string.IsNullOrEmpty(messageString))
                {
                    logger.LogInformation("Info: Received one non-empty message");

                    var messageBody = JsonConvert.DeserializeObject<MessageBody>(messageString);
                    messageBody = EnrichMessage(messageBody);

                    SaveToDb(messageBody, logger);
                }
            }
            catch (Exception ex)
            {
                logger.LogInformation($"\t{ex.Message}");
            }
        }

        private static MessageBody EnrichMessage(MessageBody messag)
        {
            messag.mirthInfo = new MirthInfo { pId = Guid.NewGuid().ToString(), name = "John Doe" };
            return messag;
        }

        private static void SaveToDb(MessageBody message, ILogger logger)
        {
            try
            {
                MongoClient mng = new MongoClient(connectionString);
                var db = mng.GetDatabase(dbName);
                var collection = db.GetCollection<MessageBody>(collectionName);
                collection.InsertOne(message);
                logger.LogInformation("Message Saved to DB");
            }
            catch(Exception ex)
            {
                logger.LogInformation($"\t{ex.Message}");
            }
        }
    }

    class MessageBody
    {
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
        public string name {get;set;}
    }
}