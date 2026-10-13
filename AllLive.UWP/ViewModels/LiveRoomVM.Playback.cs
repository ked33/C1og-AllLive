using AllLive.Core.Interface;
using AllLive.Core.Models;
using AllLive.UWP.Helper;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AllLive.UWP.ViewModels
{
    public partial class LiveRoomVM
    {
        // Keep the slot until the actual API call ends, even if its caller stops waiting.
        private readonly SemaphoreSlim playInfoGate = new SemaphoreSlim(1, 1);
        private CancellationTokenSource playUrlCts;
        private int roomLoadVersion;
        private int playUrlRequestVersion;
        private bool applyingPlayInfo;

        private bool IsRoomLoadCurrent(int version)
        {
            return isActive && version == roomLoadVersion;
        }

        public void InvalidatePlayUrlRequests()
        {
            playUrlRequestVersion++;
            var previous = playUrlCts;
            playUrlCts = null;
            // The async owner disposes its CTS after the underlying API has returned.
            if (previous != null) previous.Cancel();
        }

        public void PausePlaybackRequests()
        {
            roomLoadVersion++;
            InvalidatePlayUrlRequests();
            Loading = false;
        }

        public Task<bool> ReadLiveStatusAsync()
        {
            var site = Site;
            var roomId = RoomID;
            if (!isActive || site == null || string.IsNullOrWhiteSpace(roomId))
                throw new InvalidOperationException("No active live room.");
            return site.GetLiveStatus(roomId);
        }

        public async void LoadPlayUrl()
        {
            await LoadPlayUrlAsync(CancellationToken.None, refreshRoom: false);
        }

        public async Task<bool> LoadPlayUrlAsync(CancellationToken token, bool refreshRoom)
        {
            if (!isActive || Site == null || detail == null || token.IsCancellationRequested) return false;
            InvalidatePlayUrlRequests();
            var owner = CancellationTokenSource.CreateLinkedTokenSource(token);
            playUrlCts = owner;
            var requestVersion = playUrlRequestVersion;
            var loadVersion = roomLoadVersion;
            var site = Site;
            var roomId = RoomID;
            var roomDetail = detail;
            var quality = CurrentQuality;
            var acquired = false;
            try
            {
                acquired = await playInfoGate.WaitAsync(TimeSpan.FromSeconds(30), owner.Token);
                if (!acquired)
                {
                    LogHelper.LogDebug(() => $"[PlaybackRecovery] event=PlayInfoBusy request={requestVersion}");
                    if (!refreshRoom && IsPlayUrlRequestCurrent(owner, requestVersion, loadVersion, site))
                        Utils.ShowMessageToast("上一次播放信息请求仍在结束，请稍后重试");
                    return false;
                }
                if (!IsPlayUrlRequestCurrent(owner, requestVersion, loadVersion, site)) return false;

                List<LivePlayQuality> refreshedQualities = null;
                if (refreshRoom)
                {
                    roomDetail = await site.GetRoomDetail(roomId);
                    if (!IsPlayUrlRequestCurrent(owner, requestVersion, loadVersion, site)) return false;
                    // Only the dedicated, validated status query may declare a room offline.
                    if (roomDetail == null || !roomDetail.Status) return false;
                    refreshedQualities = await site.GetPlayQuality(roomDetail);
                    if (!IsPlayUrlRequestCurrent(owner, requestVersion, loadVersion, site)) return false;
                    if (site.Name == "虎牙直播")
                        refreshedQualities = refreshedQualities?.Where(x => x != null && x.Quality?.Contains("HDR") != true).ToList();
                    if (refreshedQualities == null || refreshedQualities.Count == 0) return false;
                    quality = refreshedQualities.FirstOrDefault(x => x.Quality == quality?.Quality) ?? refreshedQualities[0];
                }
                if (quality == null) return false;

                var data = await site.GetPlayUrls(roomDetail, quality);
                if (!IsPlayUrlRequestCurrent(owner, requestVersion, loadVersion, site)) return false;
                if (data == null || data.Count == 0)
                {
                    LogHelper.LogDebug(() => $"[PlaybackRecovery] event=EmptyPlayUrls request={requestVersion}");
                    if (!refreshRoom) Utils.ShowMessageToast("加载播放地址失败");
                    return false;
                }

                var newLines = new List<PlayurlLine>();
                foreach (var url in data)
                {
                    if (string.IsNullOrWhiteSpace(url)) continue;
                    newLines.Add(new PlayurlLine
                    {
                        Name = $"线路{newLines.Count + 1}",
                        Url = url,
                        Codec = DetectCodecLabel(url, site.Name)
                    });
                }
                if (newLines.Count == 0) return false;

                applyingPlayInfo = true;
                try
                {
                    if (refreshRoom)
                    {
                        detail = roomDetail;
                        danmakuArgs = roomDetail.DanmakuData;
                        Qualities = refreshedQualities;
                        currentQuality = quality;
                        DoPropertyChanged(nameof(CurrentQuality));
                    }
                    if (LogHelper.Enabled) LogHelper.LogDebug(() => BuildPlayUrlDebug(data));
                    allLines = newLines;
                    UpdateAvailableCodecs();
                }
                finally { applyingPlayInfo = false; }
                return true;
            }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex)
            {
                if (IsPlayUrlRequestCurrent(owner, requestVersion, loadVersion, site))
                {
                    LogHelper.LogDebug(() => $"[PlaybackRecovery] event=PlayUrlFailed request={requestVersion} error={ex.GetType().Name}");
                    if (!refreshRoom) Utils.ShowMessageToast("加载播放地址失败");
                }
                return false;
            }
            finally
            {
                if (acquired) playInfoGate.Release();
                if (playUrlCts == owner) playUrlCts = null;
                owner.Dispose();
            }
        }

        private bool IsPlayUrlRequestCurrent(CancellationTokenSource owner, int requestVersion,
            int loadVersion, ILiveSite site)
        {
            return !owner.IsCancellationRequested && playUrlCts == owner &&
                requestVersion == playUrlRequestVersion && IsRoomLoadCurrent(loadVersion) &&
                ReferenceEquals(Site, site);
        }
    }
}
