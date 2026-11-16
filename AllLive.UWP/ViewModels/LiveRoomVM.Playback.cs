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
        private CancellationTokenSource roomRequestCts;
        private int roomLoadVersion;
        private int playUrlRequestVersion;
        private bool applyingPlayInfo;
        private bool actualQualityPending;
        public string ActualQualityText
        {
            get
            {
                if (Site?.Name != "斗鱼直播") return "";
                if (actualQualityPending) return "正在确认实际画质…";
                var info = CurrentLine?.PlayInfo;
                if (info?.AppliedRate == null) return "实际画质未确认";
                var actual = string.IsNullOrWhiteSpace(info.AppliedQuality) ? "平台档位 " + info.AppliedRate : info.AppliedQuality;
                var requested = info.RequestedQuality;
                return "实际：" + actual + (info.AppliedRate != info.RequestedRate && !string.IsNullOrWhiteSpace(requested)
                    ? "（请求：" + requested + "）" : "");
            }
        }

        internal bool IsApplyingPlayInfo => applyingPlayInfo;

        private CancellationToken RoomRequestToken => roomRequestCts?.Token ?? CancellationToken.None;

        private CancellationToken ResetRoomRequests()
        {
            CancelRoomRequests();
            roomRequestCts = new CancellationTokenSource();
            return roomRequestCts.Token;
        }

        private void CancelRoomRequests()
        {
            var previous = roomRequestCts;
            roomRequestCts = null;
            if (previous == null) return;
            previous.Cancel();
            previous.Dispose();
        }

        private bool IsRoomLoadCurrent(int version)
        {
            return isActive && version == roomLoadVersion;
        }

        public void InvalidatePlayUrlRequests()
        {
            playUrlRequestVersion++;
            actualQualityPending = false;
            DoPropertyChanged(nameof(ActualQualityText));
            var previous = playUrlCts;
            playUrlCts = null;
            // The async owner disposes its CTS after the underlying API has returned.
            if (previous != null) previous.Cancel();
        }

        public void PausePlaybackRequests()
        {
            roomLoadVersion++;
            ResetRoomRequests();
            InvalidatePlayUrlRequests();
            Loading = false;
        }

        public async Task<bool> ReadLiveStatusAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            var site = Site;
            var roomId = RoomID;
            if (!isActive || site == null || string.IsNullOrWhiteSpace(roomId))
                throw new InvalidOperationException("No active live room.");
            using (var owner = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, RoomRequestToken))
                return await site.GetLiveStatus(roomId, owner.Token);
        }

        public async void LoadPlayUrl()
        {
            await LoadPlayUrlAsync(CancellationToken.None, refreshRoom: false);
        }

        public async Task<bool> LoadPlayUrlAsync(CancellationToken token, bool refreshRoom, string requiredCodec = null)
        {
            if (!isActive || Site == null || detail == null || token.IsCancellationRequested) return false;
            InvalidatePlayUrlRequests();
            var owner = CancellationTokenSource.CreateLinkedTokenSource(token, RoomRequestToken);
            playUrlCts = owner;
            var requestVersion = playUrlRequestVersion;
            var loadVersion = roomLoadVersion;
            var site = Site;
            var roomId = RoomID;
            var roomDetail = detail;
            var douyuSession = site.Name == "斗鱼直播" ? AllLive.Core.Helper.DouyuAccountState.Capture() : null;
            var quality = CurrentQuality;
            var acquired = false;
            actualQualityPending = true;
            DoPropertyChanged(nameof(ActualQualityText));
            try
            {
                acquired = await playInfoGate.WaitAsync(TimeSpan.FromSeconds(30), owner.Token);
                if (!acquired)
                {
                    LogHelper.LogDebug(() => $"[PlaybackRecovery] event=PlayInfoBusy request={requestVersion}");
                    if (!refreshRoom && IsPlayUrlRequestCurrent(owner, requestVersion, loadVersion, site, douyuSession))
                        Utils.ShowMessageToast("上一次播放信息请求仍在结束，请稍后重试");
                    return false;
                }
                if (!IsPlayUrlRequestCurrent(owner, requestVersion, loadVersion, site, douyuSession)) return false;

                List<LivePlayQuality> refreshedQualities = null;
                if (refreshRoom)
                {
                    roomDetail = await site.GetRoomDetail(roomId, owner.Token);
                    if (!IsPlayUrlRequestCurrent(owner, requestVersion, loadVersion, site, douyuSession)) return false;
                    // Only the dedicated, validated status query may declare a room offline.
                    if (roomDetail == null || !roomDetail.Status) return false;
                    refreshedQualities = await site.GetPlayQuality(roomDetail, owner.Token);
                    if (!IsPlayUrlRequestCurrent(owner, requestVersion, loadVersion, site, douyuSession)) return false;
                    if (site.Name == "虎牙直播")
                        refreshedQualities = refreshedQualities?.Where(x => x != null && x.Quality?.Contains("HDR") != true).ToList();
                    if (refreshedQualities == null || refreshedQualities.Count == 0) return false;
                    quality = refreshedQualities.FirstOrDefault(x => !string.IsNullOrEmpty(quality?.Id) && x.Id == quality.Id)
                        ?? refreshedQualities.FirstOrDefault(x => x.Quality == quality?.Quality) ?? refreshedQualities[0];
                }
                if (quality == null) return false;

                List<LivePlayUrl> playInfo = null;
                List<string> data;
                if (site is ILivePlayInfoSite metadataSite)
                {
                    playInfo = await metadataSite.GetPlayUrlsWithInfoAsync(roomDetail, quality, owner.Token);
                    data = playInfo?.Select(x => x.Url).ToList();
                }
                else data = await site.GetPlayUrls(roomDetail, quality, owner.Token);
                if (!IsPlayUrlRequestCurrent(owner, requestVersion, loadVersion, site, douyuSession)) return false;
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
                        Codec = DetectCodecLabel(url, site.Name),
                        PlayInfo = playInfo?.FirstOrDefault(x => x.Url == url)
                    });
                }
                if (newLines.Count == 0) return false;
                if (!string.IsNullOrEmpty(requiredCodec))
                {
                    // A health refresh must preserve the playing codec even when the API order changes.
                    if (!newLines.Any(x => x.Codec == requiredCodec)) return false;
                    newLines = newLines.OrderBy(x => x.Codec == requiredCodec ? 0 : 1).ToList();
                }

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
                if (IsPlayUrlRequestCurrent(owner, requestVersion, loadVersion, site, douyuSession))
                {
                    LogHelper.LogDebug(() => $"[PlaybackRecovery] event=PlayUrlFailed request={requestVersion} error={ex.GetType().Name}");
                    if (!refreshRoom) Utils.ShowMessageToast("加载播放地址失败");
                }
                return false;
            }
            finally
            {
                if (acquired) playInfoGate.Release();
                if (playUrlCts == owner)
                {
                    playUrlCts = null;
                    actualQualityPending = false;
                    DoPropertyChanged(nameof(ActualQualityText));
                }
                owner.Dispose();
            }
        }

        private bool IsPlayUrlRequestCurrent(CancellationTokenSource owner, int requestVersion,
            int loadVersion, ILiveSite site, AllLive.Core.Helper.DouyuAccountSession douyuSession)
        {
            return !owner.IsCancellationRequested && playUrlCts == owner &&
                requestVersion == playUrlRequestVersion && IsRoomLoadCurrent(loadVersion) &&
                ReferenceEquals(Site, site) &&
                (douyuSession == null || AllLive.Core.Helper.DouyuAccountState.IsCurrent(douyuSession));
        }
    }
}
