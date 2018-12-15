using MongoDB.Bson;

namespace mvc.Models
{
    public class MessageBody
    {
        public ObjectId _id { get; set; }
        public Machine machine { get; set; }
        public Ambient ambient { get; set; }
        public string timeCreated { get; set; }
        public MirthInfo mirthInfo { get; set; }
    }

    public class Machine
    {
        public double temperature { get; set; }
        public double pressure { get; set; }
    }

    public class Ambient
    {
        public double temperature { get; set; }
        public int humidity { get; set; }
    }

    public class MirthInfo
    {
        public string pId { get; set; }
        public string name { get; set; }
    }
}
