using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AllLive.SignService;

// File notifications carry rare lifecycle/settings changes; a process handle detects crashes
// without a heartbeat (which would incorrectly expire while UWP is suspended).
internal sealed class ManagedServiceOwner : IDisposable
{
    private readonly string directory;
    private readonly Process parent;
    private readonly Mutex instance;
    private readonly FileSystemWatcher watcher;
    private readonly CancellationTokenSource stopped = new();
    private readonly SemaphoreSlim changes = new(0, 1);
    private readonly Timer exitDeadline;
    private readonly Task monitor;
    private int stopRequested;
    public string SessionId { get; }
    public int ParentProcessId => parent.Id;
    public CancellationToken Token => stopped.Token;

    internal ManagedServiceOwner(string directory, OwnerState owner, Process parent, Mutex instance)
    {
        this.directory = directory;
        this.parent = parent;
        this.instance = instance;
        SessionId = owner.SessionId;
        ServiceDiagnostics.Configure(Path.Combine(directory, "..", "log"), owner.LogEnabled);
        exitDeadline = new Timer(_ => Environment.Exit(0), null, Timeout.Infinite, Timeout.Infinite);
        watcher = new FileSystemWatcher(directory, "owner.json")
        { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
        watcher.Changed += (_, _) => SignalChange();
        watcher.Created += (_, _) => SignalChange();
        watcher.Deleted += (_, _) => SignalChange();
        watcher.Renamed += (_, _) => SignalChange();
        watcher.Error += (_, _) => RequestStop(); // Fail closed if notifications become unreliable.
        watcher.EnableRaisingEvents = true;
        monitor = MonitorAsync();
        SignalChange(); // Close the read/subscribe race.
    }

    public static ManagedServiceOwner? TryCreate()
    {
        Process? parent = null;
        Mutex? mutex = null;
        try
        {
            uint length = 0;
            if (GetCurrentPackageFamilyName(ref length, null) != 122) return null;
            var family = new StringBuilder((int)length);
            if (GetCurrentPackageFamilyName(ref length, family) != 0) return null;
            var directory = Path.Combine(GetUnredirectedLocalAppData(),
                "Packages", family.ToString(), "LocalState", "LocalService");
            var owner = ReadOwner(directory);
            if (owner == null || owner.StopRequested || owner.ProtocolVersion != 1
                || !Guid.TryParseExact(owner.SessionId, "N", out _)) return null;
            parent = Process.GetProcessById(owner.ParentProcessId);
            // Hold the actual process handle, and only accept this package's UWP executable.
            var packageRoot = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
            if (parent.HasExited || !string.Equals(parent.MainModule?.FileName,
                Path.Combine(packageRoot, "AllLive.UWP.exe"), StringComparison.OrdinalIgnoreCase)) return null;
            _ = parent.Handle;
            mutex = new Mutex(false, "Local\\AllLive.SignService." + family + "." + owner.SessionId, out var created);
            if (!created) return null;
            var result = new ManagedServiceOwner(directory, owner, parent, mutex);
            parent = null;
            mutex = null;
            return result;
        }
        catch { return null; }
        finally { parent?.Dispose(); mutex?.Dispose(); }
    }

    public void PublishReady(int port)
    {
        Token.ThrowIfCancellationRequested();
        var path = Path.Combine(directory, "ready-" + SessionId + ".json");
        var temporary = Path.Combine(directory, SessionId + ".tmp");
        File.WriteAllText(temporary, JsonSerializer.Serialize(new { ProtocolVersion = 1, SessionId, Port = port }));
        File.Move(temporary, path, true);
    }

    public void RequestStop()
    {
        // Allow normal HTTP cancellation/disposal first, then bound any stuck native work.
        if (Interlocked.Exchange(ref stopRequested, 1) != 0) return;
        exitDeadline.Change(TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
        stopped.Cancel();
    }

    private void SignalChange()
    {
        try { changes.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task MonitorAsync()
    {
        try
        {
            var parentExited = parent.WaitForExitAsync(Token);
            while (!Token.IsCancellationRequested)
            {
                var changed = changes.WaitAsync(Token);
                if (await Task.WhenAny(parentExited, changed) == parentExited) { await parentExited; break; }
                await changed;
                OwnerState? owner = null;
                // A synchronous UWP write may briefly be incomplete/locked. Retry only on events.
                for (var attempt = 0; attempt < 8 && owner == null; attempt++)
                {
                    try { owner = ReadOwner(directory); }
                    catch (IOException) { }
                    catch (JsonException) { }
                    if (owner == null) await Task.Delay(25, Token);
                }
                if (owner == null || owner.SessionId != SessionId || owner.StopRequested) break;
                ServiceDiagnostics.SetEnabled(owner.LogEnabled);
            }
        }
        catch (OperationCanceledException) { }
        catch { /* Lost ownership is fatal to this companion, never to another process. */ }
        finally { RequestStop(); }
    }

    private static OwnerState? ReadOwner(string directory)
    {
        using var stream = new FileStream(Path.Combine(directory, "owner.json"), FileMode.Open,
            FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<OwnerState>(stream);
    }

    public void Dispose()
    {
        RequestStop();
        monitor.GetAwaiter().GetResult();
        watcher.Dispose();
        try { File.Delete(Path.Combine(directory, "ready-" + SessionId + ".json")); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        parent.Dispose();
        instance.Dispose();
        exitDeadline.Dispose();
        changes.Dispose();
        stopped.Dispose();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref uint length, StringBuilder? familyName);

    private static string GetUnredirectedLocalAppData()
    {
        var folder = new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
        Marshal.ThrowExceptionForHR(SHGetKnownFolderPath(ref folder, 0x00010000, IntPtr.Zero, out var path));
        try { return Marshal.PtrToStringUni(path)!; }
        finally { Marshal.FreeCoTaskMem(path); }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, IntPtr token, out IntPtr path);

    internal sealed class OwnerState
    {
        public int ProtocolVersion { get; set; }
        public string SessionId { get; set; } = "";
        public int ParentProcessId { get; set; }
        public bool StopRequested { get; set; }
        public bool LogEnabled { get; set; }
    }
}
