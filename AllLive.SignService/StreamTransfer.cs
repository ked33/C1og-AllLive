using System.Buffers;

namespace AllLive.SignService;

internal static class StreamTransfer
{
    public static async Task CopyAsync(Stream source, Stream destination, CancellationToken token,
        TimeSpan? idleTimeout = null)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            while (true)
            {
                // This bounds stalls, not the duration of a healthy live stream.
                timeout.CancelAfter(idleTimeout ?? TimeSpan.FromSeconds(30));
                var count = await source.ReadAsync(buffer.AsMemory(), timeout.Token);
                if (count == 0) return;
                timeout.CancelAfter(idleTimeout ?? TimeSpan.FromSeconds(30));
                await destination.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
