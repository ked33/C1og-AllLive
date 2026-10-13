using Jint;
using QuickJS;
using QuickJS.Native;
using System.Diagnostics;

namespace AllLive.SignService;

internal static class ScriptExecution
{
    public static Engine CreateJint(CancellationToken token) => new(options => options
        .TimeoutInterval(TimeSpan.FromSeconds(1.5)).MaxStatements(1_000_000)
        .LimitMemory(32 * 1024 * 1024).CancellationToken(token));

    public static string SignDouyin(string script, string md5, CancellationToken token)
    {
        var deadline = Stopwatch.StartNew();
        using var runtime = new QuickJSRuntime(64 * 1024 * 1024, 8 * 1024 * 1024, 512 * 1024);
        JSInterruptHandler interrupt = (_, _) => token.IsCancellationRequested || deadline.Elapsed > TimeSpan.FromSeconds(3) ? 1 : 0;
        QuickJSNativeApi.JS_SetInterruptHandler(runtime.NativeInstance, interrupt, IntPtr.Zero);
        try
        {
            using var context = runtime.CreateContext();
            var loaded = context.Eval(script, "webmssdk", JSEvalFlags.Global);
            (loaded as IDisposable)?.Dispose();
            // md5 comes from our hash function, never arbitrary script input.
            var value = context.Eval("get_sign('" + md5 + "')", "sign", JSEvalFlags.Global);
            try { return value?.ToString() ?? ""; }
            finally { (value as IDisposable)?.Dispose(); }
        }
        finally { GC.KeepAlive(interrupt); }
    }
}
