using AllLive.Core.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AllLive.Core.Interface
{
    public interface ILivePlayInfoSite
    {
        Task<List<LivePlayUrl>> GetPlayUrlsWithInfoAsync(LiveRoomDetail room, LivePlayQuality quality, CancellationToken token);
    }
}
