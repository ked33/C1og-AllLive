namespace AllLive.SignService;

internal static class ServiceDiagnostics
{
    private static readonly object sync = new();
    private static bool enabled;
    private static string? directory;

    public static void Configure(string path, bool value)
    {
        lock (sync) { directory = Path.GetFullPath(path); enabled = value; }
    }

    public static void SetEnabled(bool value) { lock (sync) enabled = value; }

    // No console/ASP.NET request logging. Do not record URLs, cookies, signatures or ownership tokens.
    public static void Write(Func<string> message)
    {
        lock (sync)
        {
            if (!enabled || directory == null) return;
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "LocalService.log");
                var file = new FileInfo(path);
                if (file.Exists && (file.Length >= 5 * 1024 * 1024 || file.LastWriteTime.Date != DateTime.Today))
                {
                    for (var index = 4; index >= 0; index--)
                    {
                        var source = index == 0 ? path : Path.Combine(directory, $"LocalService.{index}.log");
                        if (File.Exists(source)) File.Move(source, Path.Combine(directory, $"LocalService.{index + 1}.log"), true);
                    }
                }
                File.AppendAllText(path, DateTimeOffset.Now.ToString("O") + "|" + message() + Environment.NewLine);
            }
            catch { /* Diagnostics must not affect playback or shutdown. */ }
        }
    }
}
