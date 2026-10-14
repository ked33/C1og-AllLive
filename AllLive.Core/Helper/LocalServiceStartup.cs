using System;
using System.Threading;
using System.Threading.Tasks;

namespace AllLive.Core.Helper
{
    // Shared, bounded startup for all views. Starting on the thread pool also keeps
    // synchronous launch/file work and continuations off the calling UI thread.
    public sealed class LocalServiceStartup : IDisposable
    {
        private readonly object sync = new object();
        private readonly Func<int, CancellationToken, Task<LocalServiceConnection>> start;
        private readonly TimeSpan retryDelay;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private Task<LocalServiceConnection> startup;
        private int attempts;
        private bool disposed;
        private const int MaxAttempts = 2;

        public LocalServiceStartup(Func<int, CancellationToken, Task<LocalServiceConnection>> start,
            TimeSpan retryDelay)
        {
            this.start = start ?? throw new ArgumentNullException(nameof(start));
            this.retryDelay = retryDelay;
        }

        public Task<LocalServiceConnection> GetReadyAsync()
        {
            lock (sync)
            {
                if (disposed) return Task.FromCanceled<LocalServiceConnection>(new CancellationToken(true));
                if (startup == null)
                {
                    var token = lifetime.Token;
                    startup = Task.Run(() => StartWithRetryAsync(token));
                }
                return startup;
            }
        }

        private async Task<LocalServiceConnection> StartWithRetryAsync(CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int attempt;
                lock (sync)
                {
                    if (attempts >= MaxAttempts)
                        throw new InvalidOperationException("本地服务暂不可用，请在设置中点击检测重试");
                    attempt = ++attempts;
                }
                if (attempt > 1) await Task.Delay(retryDelay, token).ConfigureAwait(false);
                try
                {
                    var connection = await start(attempt, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    return connection;
                }
                catch (Exception) when (!token.IsCancellationRequested && attempt < MaxAttempts)
                {
                    // Share the retry with existing callers instead of caching the first failure.
                }
            }
        }

        public bool Invalidate(string sessionId)
        {
            lock (sync)
            {
                if (disposed || startup == null || startup.Status != TaskStatus.RanToCompletion
                    || startup.Result.SessionId != sessionId) return false;
                startup = null;
                return true;
            }
        }

        // Only an explicit user health check renews an exhausted budget, never a timer
        // or a per-room reconnect loop. Concurrent checks still share one startup.
        public bool RetryIfFailed()
        {
            lock (sync)
            {
                if (disposed || startup == null || (!startup.IsFaulted && !startup.IsCanceled)) return false;
                attempts = 0;
                startup = null;
                return true;
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                lifetime.Cancel();
                lifetime.Dispose();
            }
        }
    }
}
