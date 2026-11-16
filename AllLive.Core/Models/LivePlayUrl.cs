namespace AllLive.Core.Models
{
    // Metadata belongs to the returned URL, never to a mutable global "last request".
    public sealed class LivePlayUrl
    {
        public string Url { get; set; }
        public int? RequestedRate { get; set; }
        public string RequestedQuality { get; set; }
        public int? AppliedRate { get; set; }
        public string AppliedQuality { get; set; }
    }
}
