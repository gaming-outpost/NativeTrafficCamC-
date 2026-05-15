using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CoastalCommandCenter.Models;

namespace CoastalCommandCenter.ViewModels;

/// <summary>
/// "Rotating View" — picks a random 3×3 grid of traffic cameras from the
/// catalog and shuffles it every 45 seconds. The view layer mirrors
/// <see cref="SlideshowCameras"/> into an EmbeddedCameraGrid when
/// <see cref="SlideshowChanged"/> fires.
/// </summary>
public partial class SlideshowViewModel : ObservableObject, IDisposable
{
    public const int TileCount = 9;
    public static readonly TimeSpan ShuffleInterval = TimeSpan.FromSeconds(45);

    private readonly Random _random = new();
    private DispatcherTimer? _shuffleTimer;
    private DispatcherTimer? _indicatorTimer;
    private List<CameraItem> _catalog = [];

    public ObservableCollection<CameraItem> SlideshowCameras { get; } = [];

    [ObservableProperty] private bool _isShuffling;
    [ObservableProperty] private bool _isRunning;

    public event EventHandler? SlideshowChanged;

    public IRelayCommand ShuffleCommand { get; }

    public SlideshowViewModel()
    {
        ShuffleCommand = new RelayCommand(Shuffle);
    }

    public void SetCatalog(IReadOnlyList<CameraItem> catalog)
    {
        _catalog = catalog
            .Where(cam => !string.IsNullOrWhiteSpace(cam.StreamUrl)
                          || !string.IsNullOrWhiteSpace(cam.YoutubeUrl))
            .ToList();
    }

    public void Start()
    {
        if (IsRunning) return;

        Shuffle();

        _shuffleTimer ??= new DispatcherTimer { Interval = ShuffleInterval };
        _shuffleTimer.Tick -= OnShuffleTick;
        _shuffleTimer.Tick += OnShuffleTick;
        _shuffleTimer.Start();

        IsRunning = true;
    }

    public void Stop()
    {
        if (_shuffleTimer != null)
        {
            _shuffleTimer.Stop();
            _shuffleTimer.Tick -= OnShuffleTick;
        }

        _indicatorTimer?.Stop();
        IsShuffling = false;
        IsRunning = false;
    }

    private void OnShuffleTick(object? sender, EventArgs e) => Shuffle();

    public void Shuffle()
    {
        if (_catalog.Count == 0) return;

        var picks = PickRandomCameras(TileCount);
        SlideshowCameras.Clear();
        foreach (var cam in picks)
            SlideshowCameras.Add(cam);

        SlideshowChanged?.Invoke(this, EventArgs.Empty);
        FlashShufflingIndicator();
    }

    private void FlashShufflingIndicator()
    {
        IsShuffling = true;

        // Single-shot timer so the "shuffling…" label is visible briefly,
        // rather than only for the duration of the swap (which is instant).
        if (_indicatorTimer == null)
        {
            _indicatorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            _indicatorTimer.Tick += (_, _) =>
            {
                _indicatorTimer!.Stop();
                IsShuffling = false;
            };
        }

        _indicatorTimer.Stop();
        _indicatorTimer.Start();
    }

    private List<CameraItem> PickRandomCameras(int count)
    {
        var pool = _catalog.ToList();
        var picks = new List<CameraItem>();
        var max = Math.Min(count, pool.Count);
        for (int i = 0; i < max; i++)
        {
            var idx = _random.Next(pool.Count);
            picks.Add(pool[idx]);
            pool.RemoveAt(idx);
        }
        return picks;
    }

    public void Dispose()
    {
        Stop();
        _shuffleTimer = null;
        _indicatorTimer = null;
    }
}
