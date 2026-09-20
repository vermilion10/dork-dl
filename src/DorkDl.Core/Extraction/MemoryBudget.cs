namespace DorkDl.Core.Extraction;

// Caps concurrent in-flight payload bytes across workers.
// Workers reserve expected bytes before decompressing. Oversized bundles larger than Limit wait until in-flight bytes drop to zero, running solo rather than deadlocking.
public sealed class MemoryBudget(long limitBytes)
{
    private readonly object _gate = new();
    private long _inFlight;

    public long Limit { get; } = Math.Max(1, limitBytes);

    // Default budget: 1/4 available RAM (clamped 512 MiB to 4 GiB), leaving headroom for textures/converters.
    public static long DefaultLimit
    {
        get
        {
            var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            return Math.Clamp(available / 4, 512L << 20, 4L << 30);
        }
    }

    public static MemoryBudget Default => new(DefaultLimit);

    // Blocks until `bytes` fit under budget, then holds reservation until disposed.
    public IDisposable Reserve(long bytes, CancellationToken cancellationToken = default)
    {
        bytes = Math.Max(0, bytes);

        lock (_gate)
        {
            while (_inFlight > 0 && _inFlight + bytes > Limit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Monitor.Wait(_gate, TimeSpan.FromMilliseconds(250));
            }

            _inFlight += bytes;
        }

        return new Lease(this, bytes);
    }

    private void Release(long bytes)
    {
        lock (_gate)
        {
            _inFlight -= bytes;
            Monitor.PulseAll(_gate);
        }
    }

    private sealed class Lease(MemoryBudget owner, long bytes) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release(bytes);
            }
        }
    }
}
