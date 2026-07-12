using AllLive.Core.Models;
using System.Threading.Tasks;

namespace AllLive.Core.Interface
{
    /// <summary>
    /// 支持在播放器首次进入 Playing 后再加载观众统计的直播平台。
    /// </summary>
    public interface IDeferredAudienceMetricSite
    {
        Task<LiveAudienceMetrics> GetDeferredAudienceMetrics(LiveRoomDetail roomDetail);
    }
}
