using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using CoastalCommandCenter.Models;
using CoastalCommandCenter.Models.Health;
using CoastalCommandCenter.Services;
using CoastalCommandCenter.Services.Health;

namespace CoastalCommandCenter.Views;

/// <summary>
/// Canvas-based panel that renders every static camera across all groups as snapshot tiles.
/// Designed to be hosted inside a ScrollViewer in the Static Views tab.
/// Lazy-loaded: call <see cref="LoadAll"/> once to populate; use <see cref="RequestRefresh"/>
/// for subsequent manual refreshes.  Independent of any popout windows.
/// </summary>
internal sealed class EmbeddedStaticCameraGrid : Panel
{
    private const double TileAspect = 4.0 / 3.0;
    private const double TileGap = 1.0;
    private const int DefaultRefreshIntervalSeconds = 180;

    private double _tileWidth = 300;

    public double TileWidth
    {
        get => _tileWidth;
        set
        {
            double clamped = Math.Max(100, Math.Min(1400, value));
            if (Math.Abs(_tileWidth - clamped) < 0.5) return;
            _tileWidth = clamped;
            InvalidateMeasure();
        }
    }

    private readonly StaticSnapshotService _snapshotService;
    private readonly StreamHealthMonitor? _healthMonitor;
    private readonly Action<CameraHealthState>? _openHealthPopup;
    private readonly Canvas _canvas;
    private readonly List<StaticCameraTile> _tiles = [];

    private DispatcherTimer? _autoRefreshTimer;
    private CancellationTokenSource? _refreshCts;
    private bool _refreshLoopActive;
    private bool _refreshPending;

    public event EventHandler<string>? StatusChanged;

    public EmbeddedStaticCameraGrid(StaticSnapshotService snapshotService)
        : this(snapshotService, null, null)
    {
    }

    public EmbeddedStaticCameraGrid(
        StaticSnapshotService snapshotService,
        StreamHealthMonitor? healthMonitor,
        Action<CameraHealthState>? openHealthPopup)
    {
        _snapshotService = snapshotService;
        _healthMonitor = healthMonitor;
        _openHealthPopup = openHealthPopup;
        Background = ThemeService.GetBrush("BrushWindowBg");
        UseLayoutRounding = true;

        _canvas = new Canvas { Background = ThemeService.GetBrush("BrushBorder") };
        Children.Add(_canvas);
    }

    public int TileCount => _tiles.Count;

    public void LoadAll(IReadOnlyList<StaticCameraGroupDefinition> groups)
    {
        var allCameras = groups.SelectMany(g => g.Cameras).ToList();
        RebuildTiles(allCameras);

        int intervalSeconds = groups.Count > 0
            ? groups.Min(g => g.EffectiveRefreshIntervalSeconds)
            : DefaultRefreshIntervalSeconds;
        SetRefreshInterval(intervalSeconds);

        RequestRefresh();
    }

    public void RequestRefresh()
    {
        if (_tiles.Count == 0)
        {
            RaiseStatus("No static cameras configured.");
            return;
        }

        _refreshPending = true;
        if (_refreshLoopActive) return;

        _ = RunRefreshLoopAsync();
    }

    public void Pause()
    {
        _autoRefreshTimer?.Stop();
        CancelRefresh();
    }

    public void Resume()
    {
        if (_autoRefreshTimer != null && !_autoRefreshTimer.IsEnabled)
            _autoRefreshTimer.Start();
        RequestRefresh();
    }

    public void Teardown()
    {
        _autoRefreshTimer?.Stop();
        if (_autoRefreshTimer != null)
            _autoRefreshTimer.Tick -= OnAutoRefreshTimerTick;
        CancelRefresh();

        foreach (var tile in _tiles)
            tile.Dispose();
        _tiles.Clear();
        _canvas.Children.Clear();
    }

    private void RebuildTiles(IReadOnlyList<StaticCameraDefinition> cameras)
    {
        foreach (var tile in _tiles)
            tile.Dispose();
        _tiles.Clear();
        _canvas.Children.Clear();

        foreach (var camera in cameras)
        {
            var tile = new StaticCameraTile(camera, _healthMonitor, _openHealthPopup, isEmbedded: true);
            _tiles.Add(tile);
            _canvas.Children.Add(tile);
        }

        InvalidateMeasure();
    }

    private void SetRefreshInterval(int seconds)
    {
        int interval = seconds > 0 ? seconds : DefaultRefreshIntervalSeconds;

        _autoRefreshTimer ??= new DispatcherTimer();
        _autoRefreshTimer.Stop();
        _autoRefreshTimer.Interval = TimeSpan.FromSeconds(interval);
        _autoRefreshTimer.Tick -= OnAutoRefreshTimerTick;
        _autoRefreshTimer.Tick += OnAutoRefreshTimerTick;
        _autoRefreshTimer.Start();
    }

    private void OnAutoRefreshTimerTick(object? sender, EventArgs e)
    {
        RequestRefresh();
    }

    private async Task RunRefreshLoopAsync()
    {
        if (_refreshLoopActive) return;
        _refreshLoopActive = true;
        try
        {
            while (_refreshPending)
            {
                _refreshPending = false;
                await RefreshTilesAsync();
            }
        }
        finally
        {
            _refreshLoopActive = false;
        }
    }

    private async Task RefreshTilesAsync()
    {
        CancelRefresh();
        var cts = new CancellationTokenSource();
        _refreshCts = cts;

        foreach (var tile in _tiles)
            tile.BeginRefresh();

        RaiseStatus($"Refreshing {_tiles.Count} camera{(_tiles.Count == 1 ? "" : "s")}...");

        try
        {
            var results = await Task.WhenAll(_tiles.Select(t => t.RefreshAsync(_snapshotService, cts.Token)));
            if (!ReferenceEquals(_refreshCts, cts)) return;

            int ok = results.Count(r => r);
            RaiseStatus($"Updated {ok}/{_tiles.Count} at {DateTimeOffset.Now:h:mm:ss tt}.");
        }
        catch (OperationCanceledException)
        {
            if (!ReferenceEquals(_refreshCts, cts)) return;
            RaiseStatus("Refresh canceled.");
        }
        finally
        {
            if (ReferenceEquals(_refreshCts, cts))
            {
                _refreshCts = null;
                cts.Dispose();
            }
        }
    }

    private void CancelRefresh()
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = null;
    }

    private void RaiseStatus(string message)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => RaiseStatus(message));
            return;
        }

        StatusChanged?.Invoke(this, message);
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width;

        if (_tiles.Count == 0)
        {
            _canvas.Measure(new Size(width, 0));
            return new Size(width, 0);
        }

        int cols = Math.Max(1, (int)(width / _tileWidth));
        double slotW = width / cols;
        double slotH = slotW / TileAspect;
        int rows = (_tiles.Count + cols - 1) / cols;
        double totalH = rows * slotH;

        _canvas.Measure(new Size(width, totalH));
        return new Size(width, totalH);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double width = finalSize.Width;

        if (_tiles.Count == 0)
        {
            _canvas.Arrange(new Rect(0, 0, width, 0));
            return finalSize;
        }

        int cols = Math.Max(1, (int)(width / _tileWidth));
        double slotW = width / cols;
        double slotH = slotW / TileAspect;
        int rows = (_tiles.Count + cols - 1) / cols;
        double totalH = rows * slotH;

        for (int i = 0; i < _tiles.Count; i++)
        {
            int col = i % cols;
            int row = i / cols;
            Canvas.SetLeft(_tiles[i], col * slotW);
            Canvas.SetTop(_tiles[i], row * slotH);
            // Shrink by TileGap so canvas background shows through as thin dividers
            _tiles[i].Width = slotW - TileGap;
            _tiles[i].Height = slotH - TileGap;
        }

        _canvas.Arrange(new Rect(0, 0, width, totalH));
        return new Size(width, totalH);
    }
}
