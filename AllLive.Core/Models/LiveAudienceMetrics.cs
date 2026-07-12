namespace AllLive.Core.Models
{
    public class LiveAudienceMetrics
    {
        public long? ViewerCount { get; set; }
        public string ViewerCountSource { get; set; }
        public long? VipCount { get; set; }
        public string VipCountSource { get; set; }
        public bool AllowPopularityFallback { get; set; }
    }
}
