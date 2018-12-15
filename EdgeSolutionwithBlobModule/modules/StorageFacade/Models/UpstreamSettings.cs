namespace StorageFacade.Models
{

    public class UpstreamSettings
    {
        public int TotalMessagesLimit { get; set; }
        public int TotalSizeInKbLimit { get; set; }
        public int TemperaturePriority { get; set; }
        public int AnomalyPriority { get; set; }
        public int MirthPriority { get; set; }
    }
}