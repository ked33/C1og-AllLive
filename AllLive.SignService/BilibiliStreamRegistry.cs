namespace AllLive.SignService;

// URLs are immutable, unguessable session capabilities. Registration never overwrites another room.
internal sealed class BilibiliStreamRegistry
{
    private readonly object sync = new();
    private readonly Dictionary<string, Entry> entries = new();
    private readonly Func<DateTimeOffset> now;
    private const int Capacity = 64;
    private static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(10);
    public BilibiliStreamRegistry(Func<DateTimeOffset>? now = null) { this.now = now ?? (() => DateTimeOffset.UtcNow); }

    public string? Register(BilibiliStreamState state)
    {
        lock (sync)
        {
            Prune();
            if (entries.Count >= Capacity) return null;
            var id = Guid.NewGuid().ToString("N");
            entries.Add(id, new Entry(state, now()));
            return id;
        }
    }

    public Lease? Acquire(string id)
    {
        lock (sync)
        {
            Prune();
            if (!entries.TryGetValue(id, out var entry)) return null;
            entry.Active++;
            entry.LastUsed = now();
            return new Lease(this, id, entry.State);
        }
    }

    private void Prune()
    {
        var cutoff = now() - IdleLifetime;
        foreach (var key in entries.Where(x => x.Value.Active == 0 && x.Value.LastUsed < cutoff).Select(x => x.Key).ToArray())
            entries.Remove(key);
    }

    private void Release(string id)
    {
        lock (sync)
        {
            if (!entries.TryGetValue(id, out var entry)) return;
            entry.Active--;
            entry.LastUsed = now();
        }
    }

    private sealed class Entry(BilibiliStreamState state, DateTimeOffset time)
    {
        public BilibiliStreamState State { get; } = state;
        public DateTimeOffset LastUsed { get; set; } = time;
        public int Active { get; set; }
    }

    internal sealed class Lease(BilibiliStreamRegistry owner, string id, BilibiliStreamState state) : IDisposable
    {
        private int disposed;
        public BilibiliStreamState State { get; } = state;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) owner.Release(id); }
    }
}
