using System;
using System.Threading;
using System.Threading.Tasks;

namespace AllLive.UWP.Helper
{
    // A platform waiting for its own quota must not reserve the shared quota.
    internal sealed class FavoriteRefreshGate
    {
        private readonly SemaphoreSlim total;
        private readonly SemaphoreSlim douyin;

        internal FavoriteRefreshGate(int totalLimit, int douyinLimit)
        {
            total = new SemaphoreSlim(totalLimit, totalLimit);
            douyin = new SemaphoreSlim(douyinLimit, douyinLimit);
        }

        internal async Task<IDisposable> EnterAsync(bool isDouyin, CancellationToken token)
        {
            var platformTaken = false;
            var totalTaken = false;
            try
            {
                if (isDouyin)
                {
                    await douyin.WaitAsync(token).ConfigureAwait(false);
                    platformTaken = true;
                }
                await total.WaitAsync(token).ConfigureAwait(false);
                totalTaken = true;
                token.ThrowIfCancellationRequested();
                return new Lease(total, platformTaken ? douyin : null);
            }
            catch
            {
                if (totalTaken) total.Release();
                if (platformTaken) douyin.Release();
                throw;
            }
        }

        private sealed class Lease : IDisposable
        {
            private SemaphoreSlim total;
            private readonly SemaphoreSlim platform;

            internal Lease(SemaphoreSlim total, SemaphoreSlim platform)
            {
                this.total = total;
                this.platform = platform;
            }

            public void Dispose()
            {
                var owned = Interlocked.Exchange(ref total, null);
                if (owned == null) return;
                owned.Release();
                platform?.Release();
            }
        }
    }
}
