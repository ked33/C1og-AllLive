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
        private static CancellationTokenSource lifetime;
        private static Task<LocalServiceConnection> startup;
        private static int attempts;
        private static int openingViews;
        private static bool initialized;
        private static bool supported;

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
            if (views.Count != 0 || openingViews != 0 || lifetime == null) return;
            lifetime.Cancel();
            // This tiny synchronous write completes inside Consolidated, before UWP can suspend.
            // The service also watches the actual process handle for crash/Task Manager termination.
            WriteOwner(true);
            lifetime = null;
            startup = null;
            attempts = 0;
        }

        public static void UpdateLogging()
        {
            lock (sync)
            {
                try { if (supported && lifetime != null && !lifetime.IsCancellationRequested) WriteOwner(false); }
                catch { /* Logging/settings failures must never interrupt the application. */ }
            }
        }

        private static void WriteOwner(bool stop)
        {
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
            try { await EnsureReadyAsync(); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogHelper.LogDebug(() => "[LocalService] Startup unavailable: " + ex.GetType().Name);
            }
        }

        private static Task<LocalServiceConnection> EnsureReadyAsync()
        {
            lock (sync)
            {
                if (!supported || views.Count == 0 && openingViews == 0)
                    return Task.FromException<LocalServiceConnection>(new InvalidOperationException("本地服务当前不可用"));
                if (startup != null) return startup;
                if (attempts >= 2)
                    return Task.FromException<LocalServiceConnection>(new InvalidOperationException("本地服务启动失败，请重新打开 AllLive"));
                attempts++;
                if (lifetime != null) lifetime.Cancel();
                lifetime = new CancellationTokenSource();
                sessionId = Guid.NewGuid().ToString("N");
                WriteOwner(false);
                startup = StartAsync(sessionId, lifetime.Token, attempts > 1);
                return startup;
            }
        }

        private static async Task<LocalServiceConnection> StartAsync(string id, CancellationToken token, bool retry)
        {
            // Start outside the lock; callers share this task and do not block the UI.
            await Task.Yield();
            try { return await StartCoreAsync(id, token, retry); }
            catch
            {
                lock (sync)
                {
                    if (sessionId == id && lifetime != null)
                    {
                        lifetime.Cancel();
                        WriteOwner(true);
                    }
                }
                throw;
            }
        }

        private static async Task<LocalServiceConnection> StartCoreAsync(string id, CancellationToken token, bool retry)
        {
            if (retry) await Task.Delay(2000, token);
            token.ThrowIfCancellationRequested();
            var elapsed = Stopwatch.StartNew();
#if BUNDLED_LOCAL_SERVICE
            var launch = FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync("Managed").AsTask();
            if (await Task.WhenAny(launch, Task.Delay(TimeSpan.FromSeconds(10), token)) != launch)
            {
                _ = launch.ContinueWith(t => { var ignored = t.Exception; },
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                token.ThrowIfCancellationRequested();
                throw new TimeoutException("本地服务未能及时启动");
            }
            await launch;
#endif
            while (elapsed.Elapsed < TimeSpan.FromSeconds(10))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var ready = JObject.Parse(File.ReadAllText(Path.Combine(directory, "ready-" + id + ".json")));
                    if (ready.Value<string>("SessionId") == id && ready.Value<int>("ProtocolVersion") == 1)
                    {
                        var connection = new LocalServiceConnection(ready.Value<int>("Port"), id);
                        // The ready file proves the socket is bound. Verify AppContainer loopback too.
                        using (var client = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler
                            { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(2) })
                        using (var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, connection.BaseUrl + "/health"))
                        {
                            request.Headers.Add("X-AllLive-Session", id);
                            using (var response = await client.SendAsync(request, token))
                                response.EnsureSuccessStatusCode();
                        }
                        token.ThrowIfCancellationRequested();
                        LogHelper.LogDebug(() => "[LocalService] Ready; protocol=1");
                        return connection;
                    }
                }
                catch (IOException) { }
                catch (JsonException) { }
                await Task.Delay(200, token);
            }
            throw new TimeoutException("本地服务未能及时启动");
        }

        private static void InvalidateConnection(string id)
        {
            lock (sync)
            {
                if (id != sessionId || lifetime == null) return;
                if (attempts >= 2)
                {
                    startup = Task.FromException<LocalServiceConnection>(new InvalidOperationException("本地服务已停止，请重新打开 AllLive"));
                    return;
                }
                startup = null;
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
