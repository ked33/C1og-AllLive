using AllLive.SignService;
using AllLive.Core.Helper;
using System.Diagnostics;
using System.Text.Json;

if (args.Contains("--test-parent")) { await Task.Delay(Timeout.Infinite); return; }
var suite = new Tests();
await suite.RunAsync();
Console.WriteLine("PASS: All local service tests.");

sealed class Tests
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }

    public async Task RunAsync()
    {
        TestRoutes();
        await LocalServiceStartupTests.RunAsync();
        await TestStartupOwnerAsync();
        DesktopWindowControlTests.Run();
        TestRegistry();
        TestScripts();
        await TestTransferAsync();
        await TestOwnerAsync(0);
        await TestOwnerAsync(1);
        await TestOwnerAsync(2);
        TestLogging();
    }

    private static void TestRoutes()
    {
        Check(LocalServiceClient.IsBuiltInUrl("http://127.0.0.1:8788/api/douyu/sign"), "built-in signing endpoint");
        Check(LocalServiceClient.IsBuiltInUrl("http://localhost:8788/api/douyin/sign"), "localhost alias");
        foreach (var url in new[] { "https://example.com/api/douyu/sign", "http://127.0.0.1:8788/custom",
            "http://127.0.0.1:9000/api/douyu/sign", "http://127.0.0.1:8788/api/douyu/sign?key=custom",
            "http://user@127.0.0.1:8788/api/douyu/sign", "http://127.0.0.1.evil.test:8788/api/douyu/sign" })
            Check(!LocalServiceClient.IsBuiltInUrl(url), "preserve custom endpoint: " + url);
    }

    private static async Task TestStartupOwnerAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AllLive-startup-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "owner.json");
        try
        {
            File.WriteAllText(path, "{");
            var pending = ManagedServiceOwner.ReadStartupOwnerAsync(directory);
            Check(!pending.IsCompleted, "incomplete startup owner waits for completion");
            File.WriteAllText(path, JsonSerializer.Serialize(new ManagedServiceOwner.OwnerState
            {
                ProtocolVersion = 1, ParentProcessId = Environment.ProcessId,
                SessionId = Guid.NewGuid().ToString("N")
            }));
            Check((await pending)?.ParentProcessId == Environment.ProcessId, "startup recovers a partially written owner file");
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }

    private static void TestRegistry()
    {
        var time = DateTimeOffset.UtcNow;
        var registry = new BilibiliStreamRegistry(() => time);
        var a = new BilibiliStreamState("https://a.example/live", "", "", "");
        var b = new BilibiliStreamState("https://b.example/live", "", "", "");
        var first = registry.Register(a)!;
        var second = registry.Register(b)!;
        using var active = registry.Acquire(first)!;
        using (var other = registry.Acquire(second))
            Check(first != second && active.State == a && other!.State == b, "room registrations remain isolated");
        time += TimeSpan.FromMinutes(11);
        using (var reconnect = registry.Acquire(first)) Check(reconnect?.State == a, "active long stream is retained");
        Check(registry.Acquire(second) == null, "idle registrations expire");
        active.Dispose();
        time += TimeSpan.FromMinutes(11);
        Check(registry.Acquire(first) == null, "disconnected registration expires");
        for (var i = 0; i < 64; i++) if (registry.Register(a) == null) throw new Exception("Registry capacity mismatch");
        Check(registry.Register(a) == null, "registry cannot grow without bound");
    }

    private static void TestScripts()
    {
        var engine = ScriptExecution.CreateJint(CancellationToken.None);
        Check(engine.Evaluate("1 + 2").ToString() == "3", "Jint execution");
        var watch = Stopwatch.StartNew();
        try { engine.Execute("while (true) {}"); throw new Exception("Jint did not interrupt"); }
        catch (Exception ex) when (ex.GetType().Namespace?.StartsWith("Jint") == true) { }
        Check(watch.Elapsed < TimeSpan.FromSeconds(5), "Jint infinite loop is bounded");
        Check(ScriptExecution.SignDouyin("function get_sign(x) { return 'ok-' + x; }", "abc", CancellationToken.None) == "ok-abc",
            "native QuickJS library loads and executes");
        watch.Restart();
        using var cancel = new CancellationTokenSource(100);
        try { ScriptExecution.SignDouyin("while (true) {}", "abc", cancel.Token); throw new Exception("QuickJS did not interrupt"); }
        catch (Exception ex) when (ex.GetType().Namespace?.StartsWith("QuickJS") == true) { }
        Check(watch.Elapsed < TimeSpan.FromSeconds(5), "native execution observes cancellation");
        var sdk = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "douyin-webmssdk.js"));
        var signature = ScriptExecution.SignDouyin(sdk, "0123456789abcdef0123456789abcdef", CancellationToken.None);
        Check(!string.IsNullOrWhiteSpace(signature), "bundled Douyin script signs within budget");
    }

    private static async Task TestTransferAsync()
    {
        var bytes = new byte[250_000];
        Random.Shared.NextBytes(bytes);
        using var input = new MemoryStream(bytes);
        using var output = new MemoryStream();
        await StreamTransfer.CopyAsync(input, output, CancellationToken.None);
        Check(bytes.SequenceEqual(output.ToArray()), "stream transfer preserves bytes and EOF");
        using var stalled = new StalledStream();
        var watch = Stopwatch.StartNew();
        try { await StreamTransfer.CopyAsync(stalled, Stream.Null, CancellationToken.None, TimeSpan.FromMilliseconds(100));
            throw new Exception("Stalled read did not cancel"); }
        catch (OperationCanceledException) { }
        Check(watch.Elapsed < TimeSpan.FromSeconds(2), "stalled upstream releases transfer");
    }

    private static async Task TestOwnerAsync(int scenario)
    {
        var directory = Path.Combine(Path.GetTempPath(), "AllLive-owner-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var executable = Environment.ProcessPath!;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Tests).Assembly.Location);
        start.ArgumentList.Add("--test-parent");
        using var parent = Process.Start(start)!;
        try
        {
            var state = new ManagedServiceOwner.OwnerState { ProtocolVersion = 1, ParentProcessId = parent.Id,
                SessionId = Guid.NewGuid().ToString("N"), LogEnabled = false };
            var path = Path.Combine(directory, "owner.json");
            File.WriteAllText(path, JsonSerializer.Serialize(state));
            using var owner = new ManagedServiceOwner(directory, state, Process.GetProcessById(parent.Id), new Mutex());
            await Task.Delay(150);
            Check(!owner.Token.IsCancellationRequested, "idle owner remains alive without heartbeat");
            if (scenario == 1) parent.Kill();
            else
            {
                if (scenario == 0) state.StopRequested = true;
                else state.SessionId = Guid.NewGuid().ToString("N");
                File.WriteAllText(path, JsonSerializer.Serialize(state));
            }
            try { await Task.Delay(TimeSpan.FromSeconds(3), owner.Token); }
            catch (OperationCanceledException) { }
            Check(owner.Token.IsCancellationRequested, scenario == 1 ? "parent exit stops service"
                : scenario == 0 ? "last-view stop notification stops service" : "superseded session stops old service");
            if (scenario != 1) Check(!parent.HasExited, "service shutdown does not terminate its parent");
        }
        finally
        {
            if (!parent.HasExited) parent.Kill();
            await parent.WaitForExitAsync();
            // Only the unique directory created by this test is removed.
            File.Delete(Path.Combine(directory, "owner.json"));
            Directory.Delete(directory);
        }
    }

    private static void TestLogging()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AllLive-log-test-" + Guid.NewGuid().ToString("N"));
        ServiceDiagnostics.Configure(directory, false);
        var formatted = false;
        ServiceDiagnostics.Write(() => { formatted = true; return "disabled"; });
        Check(!formatted && !Directory.Exists(directory), "disabled logging performs no formatting or file IO");
        try
        {
            ServiceDiagnostics.SetEnabled(true);
            ServiceDiagnostics.Write(() => "enabled");
            Check(File.ReadAllText(Path.Combine(directory, "LocalService.log")).Contains("enabled"), "enabled log persists");
            ServiceDiagnostics.SetEnabled(false);
            formatted = false;
            ServiceDiagnostics.Write(() => { formatted = true; return "disabled again"; });
            Check(!formatted, "logging can be disabled at runtime");
        }
        finally { File.Delete(Path.Combine(directory, "LocalService.log")); Directory.Delete(directory); }
    }

    private sealed class StalledStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    }
}
