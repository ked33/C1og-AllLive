using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace AllLive.UWP.Helper
{
    // A lease belongs to the original operation, not to a cancellable UI wait.
    // Closing/reopening views must not accumulate native opens across the process.
    internal sealed class PlaybackOperationGate
    {
        internal const int ProcessLimit = 2;
        private static readonly SemaphoreSlim processSlots = new SemaphoreSlim(ProcessLimit, ProcessLimit);
        private readonly SemaphoreSlim pageSlot = new SemaphoreSlim(1, 1);

        internal static int ReservedProcessSlots => ProcessLimit - processSlots.CurrentCount;

        internal async Task<IDisposable> EnterAsync(TimeSpan timeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var elapsed = Stopwatch.StartNew();
            if (!await pageSlot.WaitAsync(timeout, token).ConfigureAwait(false)) return null;

            var processAcquired = false;
            var transferred = false;
            try
            {
                var remaining = timeout - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero) return null;
                processAcquired = await processSlots.WaitAsync(remaining, token).ConfigureAwait(false);
                if (!processAcquired) return null;
                token.ThrowIfCancellationRequested();
                var lease = new Lease(this);
                transferred = true;
                return lease;
            }
            finally
            {
                if (!transferred)
                {
                    if (processAcquired) processSlots.Release();
                    pageSlot.Release();
                }
            }
        }

        private sealed class Lease : IDisposable
        {
            private PlaybackOperationGate owner;

            internal Lease(PlaybackOperationGate owner)
            {
                this.owner = owner;
            }

            public void Dispose()
            {
                var previous = Interlocked.Exchange(ref owner, null);
                if (previous == null) return;
                processSlots.Release();
                previous.pageSlot.Release();
            }
        }
    }
}
