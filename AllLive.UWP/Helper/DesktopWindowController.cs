using AllLive.Core.Helper;
using Newtonsoft.Json.Linq;
using System;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace AllLive.UWP.Helper
{
    // One controller per view. Only window-size events trigger a debounced state read;
    // there is no idle polling and every request is cancelled when the page closes.
    internal sealed class DesktopWindowController : IDisposable
    {
        private static readonly Lazy<HttpClient> client = new Lazy<HttpClient>(() =>
            new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
            { Timeout = Timeout.InfiniteTimeSpan });
        private readonly Window window;
        private readonly long handle;
        private readonly Action<bool> stateChanged;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private CancellationTokenSource refresh;
        private bool toggling;
        private bool disposed;

        public DesktopWindowController(Action<bool> stateChanged)
        {
            this.stateChanged = stateChanged;
            window = Window.Current;
            handle = ((ICoreWindowInterop)(object)window.CoreWindow).WindowHandle.ToInt64();
            if (handle == 0) throw new InvalidOperationException("当前窗口不可用");
            window.SizeChanged += Window_SizeChanged;
            QueueRefresh();
        }

        public async Task ToggleAsync()
        {
            if (disposed || toggling) return;
            toggling = true;
            refresh?.Cancel();
            try
            {
                var maximized = await SendAsync(true, lifetime.Token);
                if (!disposed) stateChanged(maximized);
            }
            finally
            {
                toggling = false;
                QueueRefresh();
            }
        }

        private void Window_SizeChanged(object sender, WindowSizeChangedEventArgs e)
        {
            QueueRefresh();
        }

        private void QueueRefresh()
        {
            if (disposed || toggling) return;
            refresh?.Cancel();
            var pending = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            refresh = pending;
            _ = RefreshAsync(pending);
        }

        private async Task RefreshAsync(CancellationTokenSource pending)
        {
            try
            {
                await Task.Delay(200, pending.Token);
                var maximized = await SendAsync(false, pending.Token);
                if (!disposed && !pending.IsCancellationRequested) stateChanged(maximized);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogHelper.LogDebug(() => "[Window] State unavailable: " + ex.GetType().Name);
            }
            finally
            {
                if (refresh == pending) refresh = null;
                pending.Dispose();
            }
        }

        private async Task<bool> SendAsync(bool toggle, CancellationToken token)
        {
            // Startup has its own bounded wait. Capture this view's handle before any await.
            var ready = LocalServiceClient.ConnectionFactory();
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            LocalServiceConnection connection;
            using (token.Register(() => cancelled.TrySetCanceled()))
            {
                await Task.WhenAny(ready, cancelled.Task);
                token.ThrowIfCancellationRequested();
                connection = await ready;
            }
            token.ThrowIfCancellationRequested();
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var request = new HttpRequestMessage(HttpMethod.Post,
                connection.BaseUrl + (toggle ? "/api/window/toggle-maximize" : "/api/window/state")))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                request.Headers.Add("X-AllLive-Session", connection.SessionId);
                request.Content = new StringContent(new JObject { ["handle"] = handle }.ToString(),
                    Encoding.UTF8, "application/json");
                HttpResponseMessage response;
                try { response = await client.Value.SendAsync(request, timeout.Token); }
                catch (HttpRequestException)
                {
                    // Let the existing one-restart budget recover a dead companion on a user
                    // action. Never replay a toggle: it may already have reached the window.
                    if (toggle && !token.IsCancellationRequested)
                        LocalServiceClient.ConnectionFailed?.Invoke(connection.SessionId);
                    throw;
                }
                using (response)
                {
                    response.EnsureSuccessStatusCode();
                    return JObject.Parse(await response.Content.ReadAsStringAsync()).Value<bool>("maximized");
                }
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            window.SizeChanged -= Window_SizeChanged;
            lifetime.Cancel();
            lifetime.Dispose();
        }

        // Documented desktop COM interop for the current CoreWindow. No user32 calls
        // run inside AppContainer; all mutations are validated by the bundled service.
        [ComImport]
        [Guid("45D64A29-A63E-4CB6-B498-5781D298CB4F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ICoreWindowInterop
        {
            IntPtr WindowHandle { get; }
        }
    }
}
