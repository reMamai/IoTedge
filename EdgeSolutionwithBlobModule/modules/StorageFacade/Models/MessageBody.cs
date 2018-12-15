namespace StorageFacade.Models
{    
    using MongoDB.Bson;
    public class MessageBody
    {
        public ObjectId _id { get; set; }
        public Machine machine { get; set; }
        public Ambient ambient { get; set; }
        public string timeCreated { get; set; }
        public MirthInfo mirthInfo { get; set; }
    }
}