using CoastalCommandCenter.Models.Health;

namespace CoastalCommandCenter.Services.Health;

/// <summary>
/// Static cameras are HTTP polled by <see cref="StaticSnapshotService"/>; the
/// owning tile feeds each fetch result into <see cref="RecordFetch"/>. The
/// probe summarises a rolling window of recent fetches into a sample.
/// </summary>
public sealed class StaticSnapshotHealthProbe : StreamHealthProbeBase
{
    private sealed record FetchEntry(DateTimeOffset At, double LatencyMs, long Bytes, bool Success);

    private const int WindowSize = 10;

    private readonly object _lock = new();
    private readonly Queue<FetchEntry> _recent = new(WindowSize);

    public void RecordFetch(double latencyMs, long bytes, bool success, string? errorDetail = null)
    {
        lock (_lock)
        {
            if (_recent.Count >= WindowSize) _recent.Dequeue();
            _recent.Enqueue(new FetchEntry(DateTimeOffset.Now, latencyMs, bytes, success));
        }
        if (!success)
            NotifyTimeout(errorDetail ?? "Snapshot fetch failed");
    }

    public override ValueTask<HealthSample?> SampleAsync(CancellationToken ct)
    {
        FetchEntry[] window;
        lock (_lock)
        {
            if (_recent.Count == 0) return ValueTask.FromResult<HealthSample?>(null);
            window = _recent.ToArray();
        }

        double avgLatency = window.Average(e => e.LatencyMs);
        int failures = window.Count(e => !e.Success);
        double lossPercent = 100.0 * failures / window.Length;

        // Bitrate over the wallclock span of the window (kbps).
        var span = (window[^1].At - window[0].At).TotalSeconds;
        double bitrateKbps = 0;
        if (span > 0)
        {
            long totalBytes = window.Where(e => e.Success).Sum(e => e.Bytes);
            bitrateKbps = totalBytes * 8.0 / 1000.0 / span;
        }

        return ValueTask.FromResult<HealthSample?>(
            new HealthSample(DateTimeOffset.Now, avgLatency, lossPercent, bitrateKbps, ReconnectCount));
    }
}
