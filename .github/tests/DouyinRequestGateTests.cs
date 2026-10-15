using AllLive.Core;
using System;
using System.Threading.Tasks;

namespace AllLive.Tests
{
    public static class DouyinRequestGateTests
    {
        public static async Task<string> RunAsync()
        {
            await CheckPauseAndResumeAsync();
            await CheckExtensionNeverShortensAsync();
            CheckDefaultPauseSeconds();
            return "PASS: douyin request gate pause, resume, extension and default seconds.";
        }

        private static async Task CheckPauseAndResumeAsync()
        {
            DouyinRequestGate.BlockedPauseSecondsProvider = () => 2;
            DouyinRequestGate.CaptchaPauseSecondsProvider = () => 1;
            var pausedEvents = 0;
            EventHandler handler = (sender, args) => pausedEvents++;
            DouyinRequestGate.Paused += handler;
            try
            {
                Assert(!DouyinRequestGate.IsPaused, "Gate should start unpaused.");
                DouyinRequestGate.ReportBlocked(DouyinBlockKind.Http444, "test-444");
                Assert(DouyinRequestGate.IsPaused, "Gate should pause after a 444 report.");
                Assert(DouyinRequestGate.PauseRemainingSeconds >= 1, "Remaining seconds should be at least 1.");
                Assert(pausedEvents == 1, "Paused event should fire once.");

                var start = DateTimeOffset.UtcNow;
                await DouyinRequestGate.WaitAsync();
                var elapsed = (DateTimeOffset.UtcNow - start).TotalMilliseconds;
                Assert(!DouyinRequestGate.IsPaused, "Gate should be unpaused after waiting.");
                Assert(elapsed >= 1500, "WaitAsync returned before the pause elapsed.");
                Assert(elapsed <= 5000, "WaitAsync far exceeded the pause plus jitter bound.");

                var immediateStart = DateTimeOffset.UtcNow;
                await DouyinRequestGate.WaitAsync();
                var immediateElapsed = (DateTimeOffset.UtcNow - immediateStart).TotalMilliseconds;
                Assert(immediateElapsed < 100, "Unpaused WaitAsync should return immediately.");
            }
            finally
            {
                DouyinRequestGate.Paused -= handler;
            }
        }

        private static async Task CheckExtensionNeverShortensAsync()
        {
            DouyinRequestGate.BlockedPauseSecondsProvider = () => 3;
            DouyinRequestGate.CaptchaPauseSecondsProvider = () => 1;
            DouyinRequestGate.ReportBlocked(DouyinBlockKind.Http444, "test-444-long");
            await Task.Delay(100);
            DouyinRequestGate.ReportBlocked(DouyinBlockKind.Captcha, "test-captcha-short");
            Assert(DouyinRequestGate.PauseRemainingSeconds >= 2,
                "A shorter captcha pause must not shorten the active longer pause.");
            await DouyinRequestGate.WaitAsync();
            Assert(!DouyinRequestGate.IsPaused, "Gate should be unpaused after draining the extended pause.");
        }

        private static void CheckDefaultPauseSeconds()
        {
            DouyinRequestGate.BlockedPauseSecondsProvider = null;
            DouyinRequestGate.CaptchaPauseSecondsProvider = null;
            DouyinRequestGate.ReportBlocked(DouyinBlockKind.Http444, "test-default");
            Assert(DouyinRequestGate.PauseRemainingSeconds >= 28 && DouyinRequestGate.PauseRemainingSeconds <= 30,
                "The default pause should be 30 seconds.");
            // 不等待默认 30 秒暂停：这是最后一个用例，进程退出即结束（不再有后续请求）。
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Assertion failed: " + message);
            }
        }
    }
}
