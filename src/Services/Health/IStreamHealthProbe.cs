using CoastalCommandCenter.Models.Health;

namespace CoastalCommandCenter.Services.Health;

/// <summary>
/// Per-stream sampler. Implementations read transport-specific stats
/// (libvlc statistics, MPV process state, snapshot fetch timings) and
/// produce a normalized <see cref="HealthSample"/> on demand.
/// </summary>
public interface IStreamHealthProbe : IDisposable
{
    /// <summary>
    /// Produce one sample. Returning <see langword="null"/> means the probe
    /// has no fresh data yet (e.g. the stream just started); the monitor
    /// will simply skip this tick.
    /// </summary>
    ValueTask<HealthSample?> SampleAsync(CancellationToken ct);

    /// <summary>
    /// Optional sideband event source. Probes that observe transport-level
    /// events (reconnect, segment timeout, recovery) can surface them here;
    /// the monitor forwards them to the <see cref="CameraHealthState"/>.
    /// </summary>
    event Action<HealthEvent>? Event;
}
