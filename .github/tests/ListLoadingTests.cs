using AllLive.Core.Interface;
using AllLive.Core.Models;
using AllLive.UWP.Helper;
using AllLive.UWP.ViewModels;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AllLive.Tests
{
    public static class ListLoadingTests
    {
        public static async Task<string> RunAsync()
        {
            await CheckPlatformFairnessAsync();
            await CheckCanceledQuotasAsync();
            await CheckSearchAsync();
            return "PASS: favorite platform fairness, canceled quota release, search pages, duplicate suppression and stale query isolation.";
        }

        private static async Task CheckPlatformFairnessAsync()
        {
            var gate = new FavoriteRefreshGate(2, 1);
            using (var first = await gate.EnterAsync(true, CancellationToken.None))
            using (var cancel = new CancellationTokenSource())
            {
                var blockedPlatform = gate.EnterAsync(true, cancel.Token);
                var otherPlatform = gate.EnterAsync(false, CancellationToken.None);
                Assert(await Task.WhenAny(otherPlatform, Task.Delay(2000)) == otherPlatform,
                    "Waiting Douyin work reserved the other platform's global slot.");
                using (await otherPlatform) { }
                cancel.Cancel();
                await ExpectCancellationAsync(blockedPlatform);
            }
            using (await gate.EnterAsync(true, CancellationToken.None)) { }
        }

        private static async Task CheckCanceledQuotasAsync()
        {
            var gate = new FavoriteRefreshGate(1, 1);
            using (await gate.EnterAsync(false, CancellationToken.None))
            using (var cancel = new CancellationTokenSource())
            {
                // Owns the platform quota, but is waiting for the global quota.
                var waiting = gate.EnterAsync(true, cancel.Token);
                cancel.Cancel();
                await ExpectCancellationAsync(waiting);
            }
            using (var timeout = new CancellationTokenSource(2000))
            {
                var lease = await gate.EnterAsync(true, timeout.Token);
                lease.Dispose();
                lease.Dispose();
                using (await gate.EnterAsync(true, timeout.Token)) { }
            }
        }

        private static async Task CheckSearchAsync()
        {
            var site = new FakeSite();
            var vm = new SearchItemVM(new Site { LiveSite = site });
            site.SearchResult = Task.FromResult(Result("one"));
            vm.LoadData("first");
            Assert(site.LastPage == 1 && vm.Page == 2, "First search must request page one.");
            site.SearchResult = Task.FromResult(Result("two"));
            vm.LoadMore();
            Assert(site.LastPage == 2 && vm.Items.Count == 2 && vm.Page == 3,
                "Load more repeated page one or failed to append page two.");

            var pending = new TaskCompletionSource<LiveSearchResult>();
            site.SearchResult = pending.Task;
            vm.LoadMore();
            var oldToken = site.LastToken;
            var calls = site.SearchCalls;
            vm.LoadMore();
            Assert(site.SearchCalls == calls, "Concurrent load-more requests were admitted.");
            vm.SetQuery("second");
            Assert(oldToken.IsCancellationRequested, "A new query did not cancel the old network request.");
            site.SearchResult = Task.FromResult(Result("new-room"));
            vm.LoadData("second");
            pending.SetResult(Result("stale-room"));
            // Give the retired async continuation a chance to run; it must not touch the new list/page.
            await Task.Delay(50);
            Assert(vm.Items.Count == 1 && vm.Items[0].RoomID == "new-room" && vm.Page == 2,
                "An old query appended stale results or advanced the new page.");

            site.SearchResult = Task.FromResult(Result("new-room"));
            vm.LoadMore();
            Assert(vm.Items.Count == 1 && !vm.CanLoadMore,
                "A server repeating the same page grew the collection indefinitely.");

            var canceled = new TaskCompletionSource<LiveSearchResult>();
            site.SearchResult = canceled.Task;
            vm.Refresh();
            var canceledToken = site.LastToken;
            vm.CancelLoad();
            Assert(canceledToken.IsCancellationRequested && !vm.Loading,
                "Leaving the page failed to cancel its load.");
            canceled.SetCanceled();
            await Task.Delay(50);
        }

        private static LiveSearchResult Result(string room)
        {
            return new LiveSearchResult
            {
                Rooms = new List<LiveRoomItem> { new LiveRoomItem { RoomID = room } }, HasMore = true
            };
        }

        private static async Task ExpectCancellationAsync(Task<IDisposable> task)
        {
            try { (await task).Dispose(); }
            catch (OperationCanceledException) { return; }
            throw new InvalidOperationException("A canceled quota wait completed successfully.");
        }

        private static void Assert(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }

        private sealed class FakeSite : ILiveSite
        {
            internal Task<LiveSearchResult> SearchResult;
            internal int LastPage;
            internal int SearchCalls;
            internal CancellationToken LastToken;
            public string Name => "test";
            public ILiveDanmaku GetDanmaku() => null;
            public Task<LiveSearchResult> Search(string keyword, int page = 1, CancellationToken cancellationToken = default(CancellationToken))
            {
                SearchCalls++;
                LastPage = page;
                LastToken = cancellationToken;
                return SearchResult;
            }
            public Task<List<LiveCategory>> GetCategores(CancellationToken cancellationToken = default(CancellationToken)) => throw new NotSupportedException();
            public Task<LiveCategoryResult> GetCategoryRooms(LiveSubCategory category, int page = 1, CancellationToken cancellationToken = default(CancellationToken)) => throw new NotSupportedException();
            public Task<LiveCategoryResult> GetRecommendRooms(int page = 1, CancellationToken cancellationToken = default(CancellationToken)) => throw new NotSupportedException();
            public Task<LiveRoomDetail> GetRoomDetail(object roomId, CancellationToken cancellationToken = default(CancellationToken)) => throw new NotSupportedException();
            public Task<List<LivePlayQuality>> GetPlayQuality(LiveRoomDetail roomDetail, CancellationToken cancellationToken = default(CancellationToken)) => throw new NotSupportedException();
            public Task<List<string>> GetPlayUrls(LiveRoomDetail roomDetail, LivePlayQuality qn, CancellationToken cancellationToken = default(CancellationToken)) => throw new NotSupportedException();
            public Task<List<LiveSuperChatMessage>> GetSuperChatMessages(object roomId, CancellationToken cancellationToken = default(CancellationToken)) => throw new NotSupportedException();
            public Task<bool> GetLiveStatus(object roomId, CancellationToken cancellationToken = default(CancellationToken)) => throw new NotSupportedException();
        }
    }
}

// UI-only dependencies of the actual SearchVM/BaseViewModel are replaced in this
// isolated test assembly; their request/pagination implementation is production code.
namespace AllLive.UWP.ViewModels
{
    public sealed class Site { public ILiveSite LiveSite { get; set; } }
    public static class MainVM { public static readonly List<Site> Sites = new List<Site>(); }
}
namespace AllLive.UWP.Helper
{
    public enum LogType { ERROR }
    public static class LogHelper
    {
        public static bool IsNetworkError(Exception error) => false;
        public static void Log(string message, LogType type, Exception error) { }
    }
    public static class Utils { public static void ShowMessageToast(string message) { } }
}
