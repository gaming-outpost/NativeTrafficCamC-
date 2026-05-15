using CoastalCommandCenter.Services.Diagnostics;

namespace CoastalCommandCenter.Services;

/// <summary>
/// Process-wide bound on simultaneous tile-startup work (Media construction,
/// Parse, mpv spawn). Without this, opening a 9-tile grid saturates the network
/// and codec workers and every tile goes slow in lockstep. Default: 4.
/// </summary>
public static class TileLoadGate
{
    private static SemaphoreSlim _gate = new(4, 4);
    private static int _max = 4;
    private static bool _configured;

    public static int MaxConcurrent => _max;

    public static void Configure(int maxConcurrent)
    {
        var clamped = Math.Clamp(maxConcurrent, 1, 16);

        // Re-configuring after startup would orphan in-flight waiters on the
        // old semaphore. Allow exactly one configure call; subsequent calls
        // with the same value are a no-op, different values are a bug.
        if (_configured)
        {
            if (clamped == _max) return;
            throw new InvalidOperationException(
                $"TileLoadGate already configured to {_max}; cannot reconfigure to {clamped}.");
        }

        if (clamped != _max)
        {
            var old = _gate;
            _gate = new SemaphoreSlim(clamped, clamped);
            _max = clamped;
            try { old.Dispose(); } catch { }
        }
        _configured = true;
    }

    /// <summary>
    /// Wait for a slot. Returns an <see cref="IDisposable"/> that releases it.
    /// Cancellation propagates an <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async Task<IDisposable> AcquireAsync(string tileId, CancellationToken ct)
    {
        var current = _gate;
        var span = TileTiming.Begin(tileId, "gate.wait");
        try
        {
            await current.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            span.Dispose();
        }
        return new Releaser(current);
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _gate;
        public Releaser(SemaphoreSlim gate) => _gate = gate;
        public void Dispose()
        {
            var g = Interlocked.Exchange(ref _gate, null);
            if (g == null) return;
            try { g.Release(); } catch { }
        }
    }
}
