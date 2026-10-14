using AllLive.Core.Helper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Foundation.Metadata;
using Windows.Storage;
using Windows.System.Diagnostics;
using Windows.UI.Core;
using Windows.UI.ViewManagement;

namespace AllLive.UWP.Helper
{
    // One owner per foreground application, shared by all its views. No heartbeat/polling while idle.
    internal static class LocalServiceManager
    {
        private static readonly object sync = new object();
        private static readonly HashSet<int> views = new HashSet<int>();
        private static readonly HashSet<int> subscribedViews = new HashSet<int>();
        private static string directory;
        private static string sessionId;
        private static uint parentProcessId;
        private static LocalServiceStartup startup;
        private static int openingViews;
        private static bool initialized;
        private static bool supported;
        private static bool ownerStopped = true;

        public static bool IsSupported => supported;

        public static void Initialize()
        {
            lock (sync)
            {
                if (initialized) return;
                initialized = true;
#if BUNDLED_LOCAL_SERVICE
                supported = !Utils.IsXbox && ApiInformation.IsTypePresent("Windows.ApplicationModel.FullTrustProcessLauncher");
#endif
                if (!supported) return;
                directory = Path.Combine(ApplicationData.Current.LocalFolder.Path, "LocalService");
                parentProcessId = ProcessDiagnosticInfo.GetForCurrentProcess().ProcessId;
                LocalServiceClient.ConnectionFactory = EnsureReadyAsync;
                LocalServiceClient.ConnectionFailed = InvalidateConnection;
            }
        }

        public static void RegisterCurrentView()
        {
            if (!supported) return;
            var view = ApplicationView.GetForCurrentView();
            lock (sync)
            {
                views.Add(view.Id);
                if (subscribedViews.Add(view.Id))
                {
                    view.Consolidated += (sender, args) => UnregisterView(sender.Id);
                    CoreWindow.GetForCurrentThread().Closed += (sender, args) =>
                    {
                        lock (sync) { subscribedViews.Remove(view.Id); }
                        UnregisterView(view.Id);
                    };
                }
            }
            ObserveStartup();
        }

        public static IDisposable ReserveNewView()
        {
            lock (sync) { openingViews++; }
            return new ViewReservation();
        }

        public static void UnregisterView(int id)
        {
            lock (sync)
            {
                views.Remove(id);
                StopIfNoViews();
            }
        }

        private static void StopIfNoViews()
        {
            if (views.Count != 0 || openingViews != 0 || startup == null) return;
            startup.Dispose();
            // This tiny synchronous write completes inside Consolidated, before UWP can suspend.
            // The service also watches the actual process handle for crash/Task Manager termination.
            WriteOwner(true);
            startup = null;
            sessionId = null;
        }

        public static void UpdateLogging()
        {
            lock (sync)
            {
                try { if (supported && startup != null && sessionId != null && !ownerStopped) WriteOwner(false); }
                catch { /* Logging/settings failures must never interrupt the application. */ }
            }
        }

        private static void WriteOwner(bool stop)
        {
            ownerStopped = stop;
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "owner.json"), JsonConvert.SerializeObject(new
                {
                    ProtocolVersion = 1, SessionId = sessionId, ParentProcessId = parentProcessId,
                    StopRequested = stop, LogEnabled = LogHelper.Enabled
                }), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                LogHelper.LogDebug(() => "[LocalService] Owner update failed: " + ex.GetType().Name);
                if (!stop) throw;
            }
        }

        private static async void ObserveStartup()
        {
            try { await EnsureReadyAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogHelper.LogDebug(() => "[LocalService] Startup unavailable: " + ex.GetType().Name);
            }
        }

        public static void PrepareManualRetry(string serviceUrl)
        {
            if (!LocalServiceClient.IsBuiltInUrl(serviceUrl)) return;
            lock (sync)
            {
                if (supported && startup != null && startup.RetryIfFailed())
                    LogHelper.LogDebug(() => "[LocalService] Manual health check renewed startup budget");
            }
        }

        private static Task<LocalServiceConnection> EnsureReadyAsync()
        {
            lock (sync)
            {
                if (!supported || views.Count == 0 && openingViews == 0)
                    return Task.FromException<LocalServiceConnection>(new InvalidOperationException("本地服务当前不可用"));
                if (startup == null)
                    startup = new LocalServiceStartup(StartAttemptAsync, TimeSpan.FromSeconds(2));
                return startup.GetReadyAsync();
            }
        }

        private static async Task<LocalServiceConnection> StartAttemptAsync(int attempt, CancellationToken token)
        {
            var elapsed = Stopwatch.StartNew();
            var stage = "Owner";
            string id = null;
            try
            {
                // Called on the thread pool; file work and continuations never use the UI thread.
                lock (sync)
                {
                    token.ThrowIfCancellationRequested();
                    sessionId = id = Guid.NewGuid().ToString("N");
                    WriteOwner(false);
                }
                stage = "Launch";
                LogHelper.LogDebug(() => $"[LocalService] Starting; attempt={attempt}/2");
#if BUNDLED_LOCAL_SERVICE
                var launch = FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync("Managed").AsTask();
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    if (await Task.WhenAny(launch, Task.Delay(TimeSpan.FromSeconds(10), deadline.Token)).ConfigureAwait(false) != launch)
                    {
                        _ = launch.ContinueWith(t => { var ignored = t.Exception; },
                            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                        token.ThrowIfCancellationRequested();
                        throw new TimeoutException("系统启动本地服务进程超时");
                    }
                    deadline.Cancel();
                    await launch.ConfigureAwait(false);
                }
#endif
                token.ThrowIfCancellationRequested();
                LogHelper.LogDebug(() => $"[LocalService] Launch acknowledged; attempt={attempt}/2; elapsedMs={elapsed.ElapsedMilliseconds}");
                // Do not charge Windows activation time against cold .NET/Kestrel initialization.
                var readyElapsed = Stopwatch.StartNew();
                var readyBudget = TimeSpan.FromSeconds(20);
                Exception lastFailure = null;
                using (var client = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler
                    { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan })
                {
                    while (readyElapsed.Elapsed < readyBudget)
                    {
                        token.ThrowIfCancellationRequested();
                        stage = "Readiness";
                        try
                        {
                            var ready = JObject.Parse(File.ReadAllText(Path.Combine(directory, "ready-" + id + ".json")));
                            if (ready.Value<string>("SessionId") == id && ready.Value<int>("ProtocolVersion") == 1)
                            {
                                var connection = new LocalServiceConnection(ready.Value<int>("Port"), id);
                                stage = "Health";
                                using (var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, connection.BaseUrl + "/health"))
                                using (var check = CancellationTokenSource.CreateLinkedTokenSource(token))
                                {
                                    check.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1,
                                        Math.Min(2000, (readyBudget - readyElapsed.Elapsed).TotalMilliseconds))));
                                    request.Headers.Add("X-AllLive-Session", id);
                                    using (var response = await client.SendAsync(request, check.Token).ConfigureAwait(false))
                                        response.EnsureSuccessStatusCode();
                                }
                                token.ThrowIfCancellationRequested();
                                LogHelper.LogDebug(() => $"[LocalService] Ready; protocol=1; attempt={attempt}/2; elapsedMs={elapsed.ElapsedMilliseconds}");
                                return connection;
                            }
                        }
                        catch (IOException ex) { lastFailure = ex; }
                        catch (JsonException ex) { lastFailure = ex; }
                        catch (System.Net.Http.HttpRequestException ex) { lastFailure = ex; }
                        catch (OperationCanceledException ex)
                        {
                            token.ThrowIfCancellationRequested();
                            lastFailure = ex;
                        }
                        await Task.Delay(200, token).ConfigureAwait(false);
                    }
                }
                throw new TimeoutException(stage == "Health" ? "本地服务连接检测超时" : "等待本地服务就绪超时", lastFailure);
            }
            catch (Exception ex)
            {
                lock (sync)
                {
                    if (id != null && sessionId == id) WriteOwner(true);
                }
                if (!token.IsCancellationRequested)
                    LogHelper.LogDebug(() => $"[LocalService] Attempt failed; attempt={attempt}/2; stage={stage}; elapsedMs={elapsed.ElapsedMilliseconds}; error={ex.GetType().Name}; hresult=0x{ex.HResult:X8}; cause={ex.InnerException?.GetType().Name ?? "none"}");
                throw;
            }
        }

        private static void InvalidateConnection(string id)
        {
            lock (sync)
            {
                if (startup == null || !startup.Invalidate(id)) return;
            }
            ObserveStartup();
        }

        private sealed class ViewReservation : IDisposable
        {
            private int disposed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                lock (sync) { openingViews--; StopIfNoViews(); }
            }
        }
    }
}
