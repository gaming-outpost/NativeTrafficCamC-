using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CoastalCommandCenter.Models;
using CoastalCommandCenter.Models.Health;
using CoastalCommandCenter.Services;
using CoastalCommandCenter.Services.Health;

namespace CoastalCommandCenter.Views;

/// <summary>
/// A reusable Canvas-based camera grid that uses <see cref="CameraGridManager"/>
/// for tile layout. Can be embedded directly in any panel — no separate window needed.
/// </summary>
public sealed class EmbeddedCameraGrid : Panel
{
    private readonly VlcPlayerService _vlcService;
    private readonly MpvPlayerService _mpvService;
    private readonly StreamHealthMonitor? _healthMonitor;
    private readonly Action<CameraHealthState>? _openHealthPopup;
    private readonly Canvas _canvas;
    private readonly List<CameraGridTile> _tiles = [];

    public EmbeddedCameraGrid(VlcPlayerService vlcService, MpvPlayerService mpvService)
        : this(vlcService, mpvService, null, null)
    {
    }

    public EmbeddedCameraGrid(
        VlcPlayerService vlcService,
        MpvPlayerService mpvService,
        StreamHealthMonitor? healthMonitor,
        Action<CameraHealthState>? openHealthPopup)
    {
        _vlcService      = vlcService;
        _mpvService      = mpvService;
        _healthMonitor   = healthMonitor;
        _openHealthPopup = openHealthPopup;

        Background = ThemeService.GetBrush("BrushEmbeddedGridBackground");
        UseLayoutRounding = true;

        _canvas = new Canvas { Background = Brushes.Transparent };
        Children.Add(_canvas);

        SizeChanged += OnSizeChanged;
    }

    public int TileCount => _tiles.Count;

    /// <summary>
    /// Syncs the displayed tiles to match <paramref name="feeds"/>.
    /// Tiles for feeds already present are kept; new feeds are added; removed feeds are torn down.
    /// </summary>
    public void SyncFeeds(IReadOnlyList<LiveFeed> feeds)
    {
        SyncCameras(feeds.Select(CameraItem.FromLiveFeed).ToList());
    }

    /// <summary>
    /// Syncs the displayed tiles to match <paramref name="cameras"/>. Tiles whose
    /// <see cref="CameraItem.UniqueId"/> is already present are kept; new entries
    /// are added; removed entries are torn down.
    /// </summary>
    public void SyncCameras(IReadOnlyList<CameraItem> cameras)
    {
        var desiredIds = new HashSet<string>(cameras.Select(c => c.UniqueId), StringComparer.OrdinalIgnoreCase);

        for (int i = _tiles.Count - 1; i >= 0; i--)
        {
            var tile = _tiles[i];
            if (!desiredIds.Contains(tile.Camera.UniqueId))
            {
                tile.CloseRequested -= Tile_OnCloseRequested;
                tile.Teardown();
                _tiles.RemoveAt(i);
                _canvas.Children.Remove(tile);
            }
        }

        var existingIds = new HashSet<string>(_tiles.Select(t => t.Camera.UniqueId), StringComparer.OrdinalIgnoreCase);
        foreach (var camera in cameras)
        {
            if (!existingIds.Contains(camera.UniqueId))
            {
                var tile = new CameraGridTile(camera, _vlcService, _mpvService, _healthMonitor, _openHealthPopup);
                tile.CloseRequested += Tile_OnCloseRequested;
                _tiles.Add(tile);
                _canvas.Children.Add(tile);
            }
        }

        Reflow();
    }

    /// <summary>Tears down and removes all tiles.</summary>
    public void ClearAll()
    {
        foreach (var tile in _tiles)
        {
            tile.CloseRequested -= Tile_OnCloseRequested;
            tile.Teardown();
        }

        _tiles.Clear();
        _canvas.Children.Clear();
    }

    /// <summary>Restarts playback for all currently visible tiles.</summary>
    public void RefreshAll()
    {
        foreach (var tile in _tiles)
        {
            tile.RestartPlayback();
        }
    }

    /// <summary>Fires when a tile's close button is clicked. The feed ID is the event arg.</summary>
    public event EventHandler<string>? TileCloseRequested;

    private void Tile_OnCloseRequested(object? sender, string uniqueId)
    {
        TileCloseRequested?.Invoke(this, uniqueId);
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e) => Reflow();

    private void Reflow()
    {
        if (_tiles.Count == 0) return;

        var rects = CameraGridManager.CalculateLayout(_tiles.Count);

        double canvasW = Bounds.Width;
        double canvasH = Bounds.Height;
        if (canvasW <= 0 || canvasH <= 0) return;

        double scaleX = canvasW / CameraGridManager.GridWidth;
        double scaleY = canvasH / CameraGridManager.GridHeight;

        for (int i = 0; i < _tiles.Count; i++)
        {
            var tile = _tiles[i];
            var r = i < rects.Count ? rects[i] : rects[^1];

            double x = r.X * scaleX;
            double y = r.Y * scaleY;
            double w = r.Width * scaleX;
            double h = r.Height * scaleY;

            Canvas.SetLeft(tile, x);
            Canvas.SetTop(tile, y);
            tile.Width = w;
            tile.Height = h;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Return the available size (clamped to finite) so we fill the parent.
        var finite = new Size(
            double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);

        _canvas.Measure(finite);
        return finite;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _canvas.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
        return finalSize;
    }

    public void Teardown()
    {
        SizeChanged -= OnSizeChanged;
        ClearAll();
    }
}
