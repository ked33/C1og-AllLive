using AllLive.Core;
using System;
using System.Threading.Tasks;

namespace AllLive.Tests
{
    public static class HlsPlaybackTests
    {
        public static Task<string> RunAsync()
        {
            CheckStableProgress();
            CheckRecordedSlowProgress();
            CheckTransientAndDelayedSamples();
            CheckTimestampDiscontinuities();
            CheckRecoveryBudget();
            CheckCdnIdentity();
            return Task.FromResult("PASS: HLS stable/slow playback, timestamps, inactive windows, CDN identity and bounded recovery.");
        }

        private static void Assert(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("HLS playback: " + message);
        }

        private static void WarmUp(PlaybackHealthTracker tracker)
        {
            for (var i = 0; i < 6; i++) tracker.Observe(2, 2, true);
        }

        private static void CheckStableProgress()
        {
            var tracker = new PlaybackHealthTracker();
            var stableEvents = 0;
            for (var i = 0; i < 60; i++)
            {
                // Measured cn-jssz-cm playback: wall/progress ratio stays near one.
                var sample = tracker.Observe(2.01, 2.01 * (i % 2 == 0 ? 0.998 : 1.001), true);
                Assert(!sample.NeedsRecovery, "Normal playback caused a reconnect.");
                if (sample.Stable) stableEvents++;
                if (i < 18) Assert(!sample.Stable, "Preference learned before 30s of normal progress.");
            }
            Assert(stableEvents == 1, "Stable source must be learned once, not written every sample.");
        }

        private static void CheckRecordedSlowProgress()
        {
            var tracker = new PlaybackHealthTracker();
            WarmUp(tracker);
            // 2026-11-15 17:54:16..36, gotcha204. Includes catch-up samples, not just slow rows.
            var wall = new[] { 2.011, 2.010, 2.016, 2.014, 2.020, 2.003, 2.022, 2.029, 2.026, 2.005, 2.021 };
            var progress = new[] { 1.487, 1.495, 1.417, 1.078, 2.490, 1.299, 1.677, 2.027, 1.139, 1.247, 1.600 };
            var recovery = false;
            for (var i = 0; i < wall.Length; i++)
            {
                var sample = tracker.Observe(wall[i], progress[i], true);
                Assert(!sample.Stable, "Slow CDN incorrectly qualified for persistence.");
                recovery |= sample.NeedsRecovery && sample.Reason == "SlowProgress";
            }
            Assert(recovery, "Recorded persistent slowdown was missed.");

            tracker = new PlaybackHealthTracker();
            WarmUp(tracker);
            Assert(!tracker.Observe(2, 0, true).NeedsRecovery, "One short stall triggered recovery.");
            Assert(!tracker.Observe(2, 0, true).NeedsRecovery, "Four seconds triggered severe recovery too early.");
            Assert(tracker.Observe(2, 0, true).NeedsRecovery, "Six-second freeze was missed.");
        }

        private static void CheckTransientAndDelayedSamples()
        {
            var tracker = new PlaybackHealthTracker();
            WarmUp(tracker);
            Assert(!tracker.Observe(2, 1, true).NeedsRecovery, "An isolated slow sample triggered recovery.");
            for (var i = 0; i < 20; i++) Assert(!tracker.Observe(2, 2, true).NeedsRecovery, "Recovered playback was penalized.");
            for (var i = 0; i < 30; i++)
            {
                var hidden = tracker.Observe(2, 0, false);
                Assert(!hidden.NeedsRecovery && !hidden.Stable, "Hidden/paused/non-1x playback influenced health.");
            }
            Assert(!tracker.Observe(15, 0, true).NeedsRecovery, "Delayed UI callback was treated as a stream failure.");
            Assert(!tracker.Observe(2, null, true).NeedsRecovery, "Missing position caused a reconnect.");
            Assert(!tracker.Observe(double.NaN, 0, true).NeedsRecovery, "Invalid clock sample caused a reconnect.");
            tracker.ResetStability();
            Assert(!tracker.Observe(2, 2, true).Stable, "Buffering/pause failed to invalidate stability evidence.");
        }

        private static void CheckTimestampDiscontinuities()
        {
            var tracker = new PlaybackHealthTracker();
            Assert(!tracker.Observe(2, 5, true).NeedsRecovery, "Startup timestamp alignment triggered recovery.");
            WarmUp(tracker);
            Assert(!tracker.Observe(2, -1, true).NeedsRecovery, "One backward timestamp jump triggered recovery.");
            Assert(!tracker.Observe(2, 4, true).NeedsRecovery, "Two timestamp jumps triggered recovery.");
            var third = tracker.Observe(2, -0.5, true);
            Assert(third.NeedsRecovery && third.Reason == "TimestampDiscontinuity", "Repeated discontinuities were missed.");
            tracker.Observe(6, 0, false);
            Assert(!tracker.Observe(2, -1, true).NeedsRecovery, "Old discontinuities survived an unobserved interval.");
        }

        private static void CheckRecoveryBudget()
        {
            var budget = new HlsRecoveryBudget();
            Assert(budget.TryUse(20), "First recovery denied.");
            Assert(!budget.TryUse(79), "Cooldown was bypassed.");
            Assert(budget.TryUse(80), "Second recovery denied at cooldown boundary.");
            Assert(!budget.TryUse(1000), "Playing or elapsed time replenished the per-room recovery budget.");
            Assert(budget.Used == 2, "Budget counts denied requests.");
        }

        private static void CheckCdnIdentity()
        {
            const string first = "https://d1--cn-gotcha204.bilivideo.com/live/index.m3u8?token=old";
            const string signedAgain = "https://d1--cn-gotcha204.bilivideo.com/live/index.m3u8?token=new";
            var host = HlsCdnIdentity.GetHost(first);
            Assert(host == HlsCdnIdentity.GetHost(signedAgain) && !host.Contains("token"), "Signed URL leaked into identity.");
            Assert(HlsCdnIdentity.PreferenceRank(signedAgain, host) == 0, "Renewed URL lost exact-host preference.");
            Assert(HlsCdnIdentity.PreferenceRank("https://d1--cn-gotcha204b.bilivideo.com/x.m3u8", host) == 1, "Same-family fallback failed.");
            Assert(HlsCdnIdentity.PreferenceRank("https://d1--cn-gotcha209.bilivideo.com/x.m3u8", host) == 2, "Different gotcha groups were merged.");
            Assert(HlsCdnIdentity.GetFamily("cn-jssz-cm-02-08.bilivideo.com") == HlsCdnIdentity.GetFamily("cn-jssz-cm-02-09.bilivideo.com"), "Province/operator family mismatch.");
            Assert(HlsCdnIdentity.GetHost("https://cdn.example/live.flv?next=.m3u8") == null, "FLV was incorrectly treated as HLS.");
            Assert(HlsCdnIdentity.GetHost("not a URL") == null, "Invalid URL was accepted.");
            Assert(HlsCdnIdentity.GetFamily("gotcha204.example.org") != HlsCdnIdentity.GetFamily(host), "Unrelated CDN matched Bilibili family.");
        }
    }
}
