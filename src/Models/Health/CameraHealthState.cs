using CommunityToolkit.Mvvm.ComponentModel;

namespace CoastalCommandCenter.Models.Health;

public sealed partial class CameraHealthState : ObservableObject
{
    private readonly object _lock = new();
    private readonly Queue<HealthSample> _samples = new(HealthThresholds.SampleBufferCapacity);
    private readonly Queue<HealthEvent> _events = new(HealthThresholds.EventBufferCapacity);

    [ObservableProperty] private HealthClassification _classification = HealthClassification.Healthy;
    [ObservableProperty] private HealthSample _latestSample;
    [ObservableProperty] private DateTimeOffset _stateEnteredAt = DateTimeOffset.Now;
    [ObservableProperty] private string _classificationReason = "Awaiting first sample.";
    [ObservableProperty] private int _sampleVersion;
    [ObservableProperty] private int _eventVersion;

    public string CameraId { get; }
    public string CameraName { get; }

    public CameraHealthState(string cameraId, string cameraName)
    {
        CameraId = cameraId;
        CameraName = cameraName;
    }

    public IReadOnlyList<HealthSample> SnapshotSamples()
    {
        lock (_lock)
        {
            return _samples.ToArray();
        }
    }

    public IReadOnlyList<HealthEvent> SnapshotEvents()
    {
        lock (_lock)
        {
            return _events.Reverse().ToArray();
        }
    }

    internal void AppendSample(HealthSample sample)
    {
        lock (_lock)
        {
            if (_samples.Count >= HealthThresholds.SampleBufferCapacity)
                _samples.Dequeue();
            _samples.Enqueue(sample);
        }
        LatestSample = sample;
        SampleVersion++;
    }

    internal void AppendEvent(HealthEvent evt)
    {
        lock (_lock)
        {
            if (_events.Count >= HealthThresholds.EventBufferCapacity)
                _events.Dequeue();
            _events.Enqueue(evt);
        }
        EventVersion++;
    }

    internal void ApplyClassification(HealthClassification next, string reason, DateTimeOffset now)
    {
        ClassificationReason = reason;
        if (Classification != next)
        {
            Classification = next;
            StateEnteredAt = now;
        }
    }
}
