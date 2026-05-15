using System.Collections.Concurrent;
using CoastalCommandCenter.Models.Health;

namespace CoastalCommandCenter.Services.Health;

/// <summary>
/// Owns one async sample loop per registered camera. State objects are kept
/// alive across registrations so a brief disconnect/reconnect doesn't lose
/// history; <see cref="Unregister"/> tears state down on permanent removal.
/// </summary>
public sealed class StreamHealthMonitor : IDisposable
{
    private sealed class Registration
    {
        public required CameraHealthState State { get; init; }
        public required IStreamHealthProbe Probe { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public Task Loop { get; set; } = Task.CompletedTask;
        public int ConsecutiveTimeouts;
    }

    private readonly ConcurrentDictionary<string, Registration> _registrations = new(StringComparer.OrdinalIgnoreCase);
    private bool _showBadges;
    private bool _disposed;

    public bool ShowBadges
    {
        get => _showBadges;
        set
        {
            if (_showBadges == value) return;
            _showBadges = value;
            ShowBadgesChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<bool>? ShowBadgesChanged;

    public CameraHealthState Register(string cameraId, string cameraName, IStreamHealthProbe probe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cameraId);
        ArgumentNullException.ThrowIfNull(probe);

        if (_disposed) throw new ObjectDisposedException(nameof(StreamHealthMonitor));

        // If a registration already exists, reuse the state (preserve history)
        // but cancel the old loop and dispose the old probe.
        _registrations.TryRemove(cameraId, out var existing);
        if (existing is not null)
        {
            try { existing.Cts.Cancel(); } catch { /* ignored */ }
            try { existing.Probe.Dispose(); } catch { /* ignored */ }
        }

        var state = existing?.State ?? new CameraHealthState(cameraId, cameraName);
        var cts = new CancellationTokenSource();
        var registration = new Registration
        {
            State = state,
            Probe = probe,
            Cts = cts
        };

        Action<HealthEvent> handler = evt => state.AppendEvent(evt);
        probe.Event += handler;
        registration.Loop = Task.Run(() => RunSampleLoopAsync(registration, handler, cts.Token), cts.Token);

        _registrations[cameraId] = registration;
        return state;
    }

    public void Unregister(string cameraId)
    {
        if (string.IsNullOrWhiteSpace(cameraId)) return;
        if (_registrations.TryRemove(cameraId, out var reg))
        {
            try { reg.Cts.Cancel(); } catch { /* ignored */ }
            // Wait briefly for the sample loop to observe cancellation before
            // disposing the probe, so an in-flight SampleAsync doesn't race
            // against probe disposal.
            try { reg.Loop.Wait(TimeSpan.FromMilliseconds(250)); } catch { /* ignored */ }
            try { reg.Probe.Dispose(); } catch { /* ignored */ }
        }
    }

    public bool TryGet(string cameraId, out CameraHealthState? state)
    {
        if (_registrations.TryGetValue(cameraId, out var reg))
        {
            state = reg.State;
            return true;
        }
        state = null;
        return false;
    }

    public void RecordEvent(string cameraId, HealthEventKind kind, string detail)
    {
        if (_registrations.TryGetValue(cameraId, out var reg))
        {
            reg.State.AppendEvent(new HealthEvent(DateTimeOffset.Now, kind, detail));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var key in _registrations.Keys.ToList())
            Unregister(key);
    }

    private static async Task RunSampleLoopAsync(Registration reg, Action<HealthEvent> handler, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(HealthThresholds.SampleIntervalSeconds));
            // Sample once immediately so the badge isn't blank for a full interval.
            await SampleOnceAsync(reg, ct).ConfigureAwait(false);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await SampleOnceAsync(reg, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // expected on Unregister/Dispose
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StreamHealthMonitor] loop crashed for {reg.State.CameraId}: {ex.Message}");
        }
        finally
        {
            try { reg.Probe.Event -= handler; } catch { /* ignored */ }
        }
    }

    private static async Task SampleOnceAsync(Registration reg, CancellationToken ct)
    {
        HealthSample? sample;
        try
        {
            sample = await reg.Probe.SampleAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            reg.State.AppendEvent(new HealthEvent(DateTimeOffset.Now, HealthEventKind.Timeout, ex.Message));
            reg.ConsecutiveTimeouts++;
            ApplyClassification(reg, latest: null);
            return;
        }

        if (sample is null) return;

        reg.State.AppendSample(sample.Value);
        reg.ConsecutiveTimeouts = 0;
        ApplyClassification(reg, sample.Value);
    }

    private static void ApplyClassification(Registration reg, HealthSample? latest)
    {
        var now = DateTimeOffset.Now;

        if (reg.ConsecutiveTimeouts >= HealthThresholds.DownConsecutiveTimeouts || latest is null)
        {
            var prev = reg.State.Classification;
            reg.State.ApplyClassification(
                HealthClassification.Down,
                $"{reg.ConsecutiveTimeouts} consecutive sample failures (threshold {HealthThresholds.DownConsecutiveTimeouts}).",
                now);
            if (prev != HealthClassification.Down)
                reg.State.AppendEvent(new HealthEvent(now, HealthEventKind.StateChange, "Health state changed → Down"));
            return;
        }

        var sample = latest.Value;

        var samples = reg.State.SnapshotSamples();
        var windowStart = now.AddSeconds(-HealthThresholds.UnstableWindowSeconds);
        int reconnectsInWindow = 0;
        int? baseline = null;
        foreach (var s in samples)
        {
            if (s.Timestamp < windowStart) continue;
            baseline ??= s.ReconnectCount;
            reconnectsInWindow = Math.Max(reconnectsInWindow, s.ReconnectCount - baseline.Value);
        }

        if (reconnectsInWindow >= HealthThresholds.UnstableReconnectsInWindow)
        {
            var prev = reg.State.Classification;
            reg.State.ApplyClassification(
                HealthClassification.Unstable,
                $"{reconnectsInWindow} reconnects in last {HealthThresholds.UnstableWindowSeconds}s (threshold {HealthThresholds.UnstableReconnectsInWindow}).",
                now);
            if (prev != HealthClassification.Unstable)
                reg.State.AppendEvent(new HealthEvent(now, HealthEventKind.StateChange, "Health state changed → Unstable"));
            return;
        }

        if (sample.LossPercent > HealthThresholds.MaxHealthyLossPercent)
        {
            var prev = reg.State.Classification;
            reg.State.ApplyClassification(
                HealthClassification.Degraded,
                $"Loss {sample.LossPercent:0.0}% exceeds Degraded threshold ({HealthThresholds.MaxHealthyLossPercent:0.0}%).",
                now);
            if (prev != HealthClassification.Degraded)
                reg.State.AppendEvent(new HealthEvent(now, HealthEventKind.StateChange, "Health state changed → Degraded"));
            return;
        }
        if (sample.LatencyMs > HealthThresholds.MaxHealthyLatencyMs)
        {
            var prev = reg.State.Classification;
            reg.State.ApplyClassification(
                HealthClassification.Degraded,
                $"Latency {sample.LatencyMs:0} ms exceeds Degraded threshold ({HealthThresholds.MaxHealthyLatencyMs:0} ms).",
                now);
            if (prev != HealthClassification.Degraded)
                reg.State.AppendEvent(new HealthEvent(now, HealthEventKind.StateChange, "Health state changed → Degraded"));
            return;
        }

        {
            var prev = reg.State.Classification;
            reg.State.ApplyClassification(
                HealthClassification.Healthy,
                $"Loss {sample.LossPercent:0.0}% / latency {sample.LatencyMs:0} ms within healthy thresholds.",
                now);
            if (prev != HealthClassification.Healthy && prev != HealthClassification.Down)
                reg.State.AppendEvent(new HealthEvent(now, HealthEventKind.Recovery, "Feed recovered"));
            if (prev != HealthClassification.Healthy)
                reg.State.AppendEvent(new HealthEvent(now, HealthEventKind.StateChange, "Health state changed → Healthy"));
        }
    }
}
