using AllLive.Core;
using AllLive.UWP.Helper;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AllLive.Tests
{
    public static class PlaybackRecoveryTests
    {
        private static readonly TimeSpan ShortWait = TimeSpan.FromMilliseconds(50);
        private static readonly TimeSpan NormalWait = TimeSpan.FromSeconds(5);

        public static async Task<string> RunAsync()
        {
            await CheckPageAndProcessLimitsAsync();
            await CheckCancellationAsync();
            await CheckConcurrentOpensAsync();
            CheckStatusParsing();
            Assert(PlaybackOperationGate.ReservedProcessSlots == 0, "Leaked process slots.");
            return "PASS: native open limits, cancellation, timeout, lease lifetime, concurrency and live status parsing.";
        }

        private static async Task CheckPageAndProcessLimitsAsync()
        {
            var firstPage = new PlaybackOperationGate();
            var secondPage = new PlaybackOperationGate();
            var thirdPage = new PlaybackOperationGate();
            var first = await firstPage.EnterAsync(NormalWait, CancellationToken.None);
            var second = await secondPage.EnterAsync(NormalWait, CancellationToken.None);
            try
            {
                Assert(first != null && second != null, "Independent pages must be allowed to open.");
                Assert(PlaybackOperationGate.ReservedProcessSlots == 2, "Process limit must be two.");
                Assert(await firstPage.EnterAsync(ShortWait, CancellationToken.None) == null, "Same page opened twice.");
                Assert(await thirdPage.EnterAsync(ShortWait, CancellationToken.None) == null, "Process limit exceeded.");
            }
            finally
            {
                first?.Dispose();
                first?.Dispose(); // An async cleanup may be requested more than once.
                second?.Dispose();
            }
            using (var next = await thirdPage.EnterAsync(NormalWait, CancellationToken.None))
                Assert(next != null, "A timeout leaked the page slot.");
        }

        private static async Task CheckCancellationAsync()
        {
            var page = new PlaybackOperationGate();
            var held = await page.EnterAsync(NormalWait, CancellationToken.None);
            using (var canceled = new CancellationTokenSource())
            {
                var waiting = page.EnterAsync(NormalWait, canceled.Token);
                canceled.Cancel();
                await ExpectCancellationAsync(waiting);
                Assert(PlaybackOperationGate.ReservedProcessSlots == 1, "Canceling a wait released an active operation.");
                Assert(await page.EnterAsync(ShortWait, CancellationToken.None) == null, "Canceled UI wait allowed overlapping opens.");
            }
            held.Dispose();

            var page2 = new PlaybackOperationGate();
            var page3 = new PlaybackOperationGate();
            using (var slot1 = await page.EnterAsync(NormalWait, CancellationToken.None))
            using (var slot2 = await page2.EnterAsync(NormalWait, CancellationToken.None))
            using (var canceled = new CancellationTokenSource())
            {
                // This waiter has acquired its page slot and is waiting for the process slot.
                var waiting = page3.EnterAsync(NormalWait, canceled.Token);
                canceled.Cancel();
                await ExpectCancellationAsync(waiting);
                Assert(PlaybackOperationGate.ReservedProcessSlots == 2, "Cancellation released another page's lease.");
            }
            using (var afterCancellation = await page3.EnterAsync(NormalWait, CancellationToken.None))
                Assert(afterCancellation != null, "Canceling the process wait leaked a page slot.");

            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                await ExpectCancellationAsync(page.EnterAsync(NormalWait, canceled.Token));
            }
        }

        private static async Task CheckConcurrentOpensAsync()
        {
            var pages = new[] { new PlaybackOperationGate(), new PlaybackOperationGate(), new PlaybackOperationGate() };
            var activeByPage = new int[pages.Length];
            var activeTotal = 0;
            var work = new List<Task>();
            for (var index = 0; index < 60; index++)
            {
                var pageIndex = index % pages.Length;
                work.Add(Task.Run(async () =>
                {
                    using (var lease = await pages[pageIndex].EnterAsync(NormalWait, CancellationToken.None))
                    {
                        Assert(lease != null, "Concurrent open timed out.");
                        Assert(Interlocked.Increment(ref activeByPage[pageIndex]) == 1, "Overlapping native opens on one page.");
                        Assert(Interlocked.Increment(ref activeTotal) <= 2, "More than two process-wide native opens.");
                        await Task.Delay(2);
                        Interlocked.Decrement(ref activeTotal);
                        Interlocked.Decrement(ref activeByPage[pageIndex]);
                    }
                }));
            }
            await Task.WhenAll(work);
        }

        private static void CheckStatusParsing()
        {
            Assert(LiveStatusParser.IsLive(new JValue(1), 1, 0, 2), "Bilibili live status.");
            Assert(!LiveStatusParser.IsLive(new JValue(2), 1, 0, 2), "Bilibili recorded stream is offline.");
            Assert(LiveStatusParser.IsLive(new JValue("2"), 2, 0, 1), "Huya numeric string status.");
            Assert(!LiveStatusParser.IsLive(new JValue(4), 2, 4), "Douyin ended status.");
            Assert(LiveStatusParser.IsTwitchLive(new JObject()), "Twitch stream object is live.");
            Assert(!LiveStatusParser.IsTwitchLive(JValue.CreateNull()), "Explicit Twitch stream null is offline.");
            ExpectUnknown(() => LiveStatusParser.IsLive(null, 2, 4));
            ExpectUnknown(() => LiveStatusParser.IsLive(JValue.CreateNull(), 2, 4));
            ExpectUnknown(() => LiveStatusParser.IsLive(new JValue(true), 1, 0));
            ExpectUnknown(() => LiveStatusParser.IsLive(new JValue("blocked"), 2, 4));
            ExpectUnknown(() => LiveStatusParser.IsLive(new JValue(99), 2, 4));
            ExpectUnknown(() => LiveStatusParser.IsTwitchLive(null));
            ExpectUnknown(() => LiveStatusParser.IsTwitchLive(new JArray()));
        }

        private static async Task ExpectCancellationAsync(Task<IDisposable> task)
        {
            try { (await task)?.Dispose(); }
            catch (OperationCanceledException) { return; }
            throw new InvalidOperationException("Expected a canceled wait.");
        }

        private static void ExpectUnknown(Action action)
        {
            try { action(); }
            catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("Invalid/missing live status was treated as a valid status.");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
