using AllLive.Core;
using AllLive.UWP.Helper;
using AllLive.UWP.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Windows.Media.Playback;

namespace AllLive.UWP.Views
{
    public sealed partial class LiveRoomPage
    {
        private PlaybackHealthTracker playbackHealth = new PlaybackHealthTracker();
        // Deliberately not reset by Playing, automatic retries, or a new signed URL.
        private readonly HlsRecoveryBudget hlsRecoveryBudget = new HlsRecoveryBudget();
        private readonly Stopwatch hlsRecoveryClock = Stopwatch.StartNew();
        private readonly HashSet<string> unhealthyHlsHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool applyingRecoveryLine;

        private bool IsBilibiliHlsPlayback()
        {
            return liveRoomVM?.SiteName == "哔哩哔哩直播" && HlsCdnIdentity.GetHost(lastPlaybackUrl) != null;
        }

        private void HandlePlaybackHealth(PlaybackSampleResult sample, int attemptVersion)
        {
            if (sample?.Health == null || !IsMediaSourceAttemptCurrent(attemptVersion)) return;
            var health = sample.Health;
            if (health.Stable)
            {
                // Playing alone does not prove stability: require 30s of uninterrupted normal progress.
                streamReconnectAttempt = 0;
                recoveryElapsed = null;
                recoveryGeneration++;
                LogDebugIfEnabled("连续正常推进 30s，重置重连预算");
                if (IsBilibiliHlsPlayback())
                {
                    unhealthyHlsHosts.Remove(HlsCdnIdentity.GetHost(sample.Url));
                    PlaybackCdnPreferenceStore.Remember(liveRoomVM.SiteName, liveRoomVM.detail?.RoomID, liveRoomVM.CurrentLine);
                }
            }

            if (!health.NeedsRecovery || !IsBilibiliHlsPlayback() || !isPlaybackViewVisible ||
                isNetworkDown || playbackRecoveryStopped || streamReconnectInProgress != 0 ||
                mediaPlayer?.PlaybackSession?.PlaybackState != MediaPlaybackState.Playing) return;

            var current = liveRoomVM.CurrentLine;
            var host = HlsCdnIdentity.GetHost(current?.Url);
            if (host == null) return;
            if (unhealthyHlsHosts.Add(host))
                PlaybackCdnPreferenceStore.Forget(liveRoomVM.SiteName, liveRoomVM.detail?.RoomID, current);
            if (!hlsRecoveryBudget.TryUse(hlsRecoveryClock.Elapsed.TotalSeconds)) return;

            // Keep quality and codec. Prefer another CDN family; do not ping-pong between bad hosts.
            var target = liveRoomVM.Lines?
                .Where(x => x.Codec == current.Codec && HlsCdnIdentity.GetHost(x.Url) != null &&
                    HlsCdnIdentity.GetHost(x.Url) != host && !unhealthyHlsHosts.Contains(HlsCdnIdentity.GetHost(x.Url)))
                .OrderBy(x => HlsCdnIdentity.GetFamily(HlsCdnIdentity.GetHost(x.Url)) == HlsCdnIdentity.GetFamily(host) ? 1 : 0)
                .FirstOrDefault();

            LogDebugIfEnabled(() => $"[HlsHealth] event=Recovery reason={health.Reason} " +
                $"source={attemptVersion} used={hlsRecoveryBudget.Used}/{HlsRecoveryBudget.Limit} " +
                $"host={host} nextHost={HlsCdnIdentity.GetHost(target?.Url) ?? "refresh"} " +
                $"windowMs={health.WindowSeconds * 1000:F0} progressMs={health.WindowProgressSeconds * 1000:F0}");
            TryScheduleStreamReconnect("HlsHealth/" + health.Reason, target);
        }

        private void SelectRecoveryLine(PlayurlLine line)
        {
            applyingRecoveryLine = true;
            try { liveRoomVM.CurrentLine = line; }
            finally { applyingRecoveryLine = false; }
        }

        private bool TrySelectRecoveryCodec(string reason)
        {
            applyingRecoveryLine = true;
            try { return TryFallbackBilibiliCodec(reason); }
            finally { applyingRecoveryLine = false; }
        }

        private void ForgetCurrentCdnPreference()
        {
            if (IsBilibiliHlsPlayback())
                PlaybackCdnPreferenceStore.Forget(liveRoomVM.SiteName, liveRoomVM.detail?.RoomID, liveRoomVM.CurrentLine);
        }
    }
}
