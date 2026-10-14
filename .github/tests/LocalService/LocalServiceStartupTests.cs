using AllLive.Core.Helper;

internal static class LocalServiceStartupTests
{
    private static LocalServiceConnection Connection() => new(12345, Guid.NewGuid().ToString("N"));

    internal static async Task RunAsync()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var callerThread = Environment.CurrentManagedThreadId;
        using (var shared = new LocalServiceStartup(async (attempt, token) =>
        {
            Check(Environment.CurrentManagedThreadId != callerThread, "startup leaves caller thread");
            Interlocked.Increment(ref calls);
            entered.TrySetResult(true);
            await release.Task;
            return Connection();
        }, TimeSpan.Zero))
        {
            var first = shared.GetReadyAsync();
            await entered.Task;
            var waiting = Enumerable.Range(0, 20).Select(_ => shared.GetReadyAsync()).ToArray();
            Check(waiting.All(t => ReferenceEquals(t, first)), "concurrent views share startup");
            Check(!shared.RetryIfFailed(), "manual check does not replace pending startup");
            release.SetResult(true);
            var connection = await first;
            Check(calls == 1 && !shared.Invalidate(Guid.NewGuid().ToString("N")), "stale connection errors do not restart service");
            Check(shared.Invalidate(connection.SessionId), "current connection may invalidate");
            await shared.GetReadyAsync();
            Check(calls == 2, "connection failure uses the remaining startup budget");
        }

        calls = 0;
        using (var transient = new LocalServiceStartup((attempt, token) =>
        {
            Interlocked.Increment(ref calls);
            return attempt == 1 ? Task.FromException<LocalServiceConnection>(new TimeoutException())
                : Task.FromResult(Connection());
        }, TimeSpan.Zero))
        {
            await transient.GetReadyAsync();
            Check(calls == 2, "first startup timeout retries within the shared task");
        }

        calls = 0;
        var fail = true;
        using (var recoverable = new LocalServiceStartup((attempt, token) =>
        {
            Interlocked.Increment(ref calls);
            return fail ? Task.FromException<LocalServiceConnection>(new IOException()) : Task.FromResult(Connection());
        }, TimeSpan.Zero))
        {
            try { await recoverable.GetReadyAsync(); throw new Exception("Expected failure"); }
            catch (IOException) { }
            var failed = recoverable.GetReadyAsync();
            Check(failed.IsFaulted && calls == 2, "failure cannot cause unbounded automatic launches");
            Check(recoverable.RetryIfFailed(), "manual check clears exhausted startup");
            fail = false;
            await recoverable.GetReadyAsync();
            Check(calls == 3, "manual check recovers without restarting AllLive");
        }

        entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        calls = 0;
        using (var stopped = new LocalServiceStartup(async (attempt, token) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, token);
            return Connection();
        }, TimeSpan.Zero))
        {
            var pending = stopped.GetReadyAsync();
            await entered.Task;
            stopped.Dispose();
            try { await pending; throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { }
            Check(calls == 1 && stopped.GetReadyAsync().IsCanceled, "last-view shutdown cancels startup without retry");
        }
        Console.WriteLine("PASS: bounded startup, UI-thread isolation, shared retries, manual recovery and shutdown.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
    }
}
