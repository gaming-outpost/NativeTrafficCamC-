using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CoastalCommandCenter.Models.Health;

namespace CoastalCommandCenter.ViewModels.Health;

/// <summary>
/// Backs <see cref="Views.StreamHealthPopup"/>. Wraps a <see cref="CameraHealthState"/>
/// and surfaces its samples + events as ObservableCollection-friendly views, plus a
/// derived "time-in-state" string that ticks once per second.
/// </summary>
public sealed partial class StreamHealthPopupViewModel : ObservableObject, IDisposable
{
    private readonly CameraHealthState _state;
    private readonly DispatcherTimer _tick;
    private bool _disposed;

    [ObservableProperty] private string _cameraName;
    [ObservableProperty] private HealthClassification _classification;
    [ObservableProperty] private string _classificationReason = string.Empty;
    [ObservableProperty] private string _timeInStateLabel = string.Empty;

    public CameraHealthState State => _state;
    public ObservableCollection<HealthEvent> Events { get; } = new();

    public StreamHealthPopupViewModel(CameraHealthState state)
    {
        _state = state;
        _cameraName = state.CameraName;
        _classification = state.Classification;
        _classificationReason = state.ClassificationReason;

        _state.PropertyChanged += OnStateChanged;
        RefreshEventList();
        UpdateTimeInState();

        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => UpdateTimeInState();
        _tick.Start();
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnStateChanged(sender, e));
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(CameraHealthState.Classification):
                Classification = _state.Classification;
                UpdateTimeInState();
                break;
            case nameof(CameraHealthState.ClassificationReason):
                ClassificationReason = _state.ClassificationReason;
                break;
            case nameof(CameraHealthState.EventVersion):
                RefreshEventList();
                break;
        }
    }

    private void RefreshEventList()
    {
        var events = _state.SnapshotEvents();
        Events.Clear();
        foreach (var evt in events)
            Events.Add(evt);
    }

    private void UpdateTimeInState()
    {
        var span = DateTimeOffset.Now - _state.StateEnteredAt;
        if (span.TotalSeconds < 0) span = TimeSpan.Zero;
        TimeInStateLabel = span switch
        {
            { TotalHours: >= 1 } => $"{(int)span.TotalHours}h {span.Minutes}m",
            { TotalMinutes: >= 1 } => $"{(int)span.TotalMinutes}m {span.Seconds}s",
            _ => $"{(int)span.TotalSeconds}s"
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tick.Stop();
        _state.PropertyChanged -= OnStateChanged;
    }
}
