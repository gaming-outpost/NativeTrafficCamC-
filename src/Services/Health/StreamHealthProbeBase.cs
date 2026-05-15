using CoastalCommandCenter.Models.Health;

namespace CoastalCommandCenter.Services.Health;

/// <summary>
/// Common bookkeeping shared by all probes: a thread-safe reconnect counter
/// (poked by the owning tile when it tears down/restarts playback) and an
/// event fan-out that the monitor subscribes to.
/// </summary>
public abstract class StreamHealthProbeBase : IStreamHealthProbe
{
    private int _reconnectCount;
    private bool _disposed;

    public int ReconnectCount => Volatile.Read(ref _reconnectCount);

    public event Action<HealthEvent>? Event;

    public void NotifyReconnect(string detail)
    {
        Interlocked.Increment(ref _reconnectCount);
        RaiseEvent(new HealthEvent(DateTimeOffset.Now, HealthEventKind.Reconnect, detail));
    }

    public void NotifyTimeout(string detail)
        => RaiseEvent(new HealthEvent(DateTimeOffset.Now, HealthEventKind.Timeout, detail));

    public void NotifyRecovery(string detail)
        => RaiseEvent(new HealthEvent(DateTimeOffset.Now, HealthEventKind.Recovery, detail));

    protected void RaiseEvent(HealthEvent evt) => Event?.Invoke(evt);

    public abstract ValueTask<HealthSample?> SampleAsync(CancellationToken ct);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeCore();
    }

    protected virtual void DisposeCore() { }
}
