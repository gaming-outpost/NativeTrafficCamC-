using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using CoastalCommandCenter.Models;
using CoastalCommandCenter.Models.Health;
using CoastalCommandCenter.Services;
using CoastalCommandCenter.Services.Diagnostics;
using CoastalCommandCenter.Services.Health;
using LibVLCSharp.Avalonia;
using LibVLCSharp.Shared;

namespace CoastalCommandCenter.Views;

/// <summary>
/// A single OS window that hosts all camera tiles in a managed grid layout.
/// Cameras are added/removed at runtime; the layout reflows automatically.
/// The window is draggable via its title bar as a single unit.
/// </summary>
public partial class CameraGridWindow : Window
{
    private const double TitleBarHeight = 30;

    private readonly VlcPlayerService     _vlcService;
    private readonly MpvPlayerService     _mpvService;
    private readonly StreamHealthMonitor? _healthMonitor;
    private readonly Action<CameraHealthState>? _openHealthPopup;
    private readonly Canvas               _canvas;
    private readonly TextBlock            _titleText;
    private readonly Border               _resizeGrip;

    // Ordered list of active tiles.
    private readonly List<CameraGridTile> _tiles = [];

    // Drag state (title-bar drag moves the whole window).
    private bool   _isDragging;
    private Point  _dragStartPointer;   // pointer position relative to window when drag began
    private PixelPoint _dragStartWindowPos;
    private double _dragScale;          // cached screen scaling during drag

    // Fullscreen state.
    private bool          _isFullscreen;
    private PixelPoint    _restorePos;
    private double        _restoreW;
    private double        _restoreH;

    public event EventHandler? CaptureShortcutPressed;

    // ──────────────────────────────────────────────────────────────────────────
    // Construction
    // ──────────────────────────────────────────────────────────────────────────

    // Public parameterless ctor keeps runtime XAML loading/designer tooling happy.
    public CameraGridWindow() : this(new VlcPlayerService(), new MpvPlayerService(), null, null)
    {
    }

    public CameraGridWindow(VlcPlayerService vlcService, MpvPlayerService mpvService)
        : this(vlcService, mpvService, null, null)
    {
    }

    public CameraGridWindow(
        VlcPlayerService vlcService,
        MpvPlayerService mpvService,
        StreamHealthMonitor? healthMonitor,
        Action<CameraHealthState>? openHealthPopup)
    {
        _vlcService      = vlcService;
        _mpvService      = mpvService;
        _healthMonitor   = healthMonitor;
        _openHealthPopup = openHealthPopup;

        InitializeComponent();

        _canvas    = this.FindControl<Canvas>("CameraCanvas")    ?? throw new InvalidOperationException("CameraCanvas not found.");
        _titleText = this.FindControl<TextBlock>("GridTitleText") ?? throw new InvalidOperationException("GridTitleText not found.");
        _resizeGrip = this.FindControl<Border>("ResizeGrip") ?? throw new InvalidOperationException("ResizeGrip not found.");

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        SizeChanged += OnSizeChanged;
        UpdateResizeGripState();
    }

    public void ApplyThemeMode(AppThemeMode mode)
    {
        // Theme is applied globally via ThemeService brush mutations.
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e) => Reflow();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // ──────────────────────────────────────────────────────────────────────────
    // Public API — called by MainWindow
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>Number of camera tiles currently shown.</summary>
    public int TileCount => _tiles.Count;

    public IReadOnlyList<CameraItem> GetVisibleCameras() =>
        _tiles.Select(tile => tile.Camera).ToList();

    /// <summary>Returns <see langword="true"/> if a tile for <paramref name="uniqueId"/> already exists.</summary>
    public bool HasCamera(string uniqueId) =>
        _tiles.Any(t => t.Camera.UniqueId == uniqueId);

    /// <summary>
    /// Adds a camera tile to the grid, then reflows all tiles.
    /// No-op if a tile for this camera is already present.
    /// </summary>
    public void AddCamera(CameraItem camera)
    {
        if (HasCamera(camera.UniqueId))
        {
            // Bring focus to existing tile (visual pulse optional).
            return;
        }

        var tile = new CameraGridTile(camera, _vlcService, _mpvService, _healthMonitor, _openHealthPopup);
        tile.CloseRequested += Tile_OnCloseRequested;
        _tiles.Add(tile);
        _canvas.Children.Add(tile);

        Reflow();
        UpdateTitle();
    }

    /// <summary>
    /// Removes the tile for <paramref name="uniqueId"/> and reflows remaining tiles.
    /// Hides the window when the last tile is removed.
    /// </summary>
    public void RemoveCamera(string uniqueId)
    {
        var tile = _tiles.FirstOrDefault(t => t.Camera.UniqueId == uniqueId);
        if (tile == null) return;

        tile.CloseRequested -= Tile_OnCloseRequested;
        tile.Teardown();
        _tiles.Remove(tile);
        _canvas.Children.Remove(tile);

        if (_tiles.Count == 0)
        {
            Hide();
        }
        else
        {
            Reflow();
            UpdateTitle();
        }
    }

    /// <summary>Tears down and removes all tiles, then hides the window.</summary>
    public void CloseAll()
    {
        foreach (var tile in _tiles.ToList())
        {
            tile.CloseRequested -= Tile_OnCloseRequested;
            tile.Teardown();
        }

        _tiles.Clear();
        _canvas.Children.Clear();
        Hide();
    }

    /// <summary>Restarts playback for every visible camera tile in the grid.</summary>
    public void RefreshAll()
    {
        foreach (var tile in _tiles)
        {
            tile.RestartPlayback();
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Layout
    // ──────────────────────────────────────────────────────────────────────────

    private void Reflow()
    {
        var rects = CameraGridManager.CalculateLayout(_tiles.Count);

        // Use Bounds (actual rendered size) — canvas has no explicit Width/Height,
        // it stretches to fill the window's content area via the Grid row definition.
        double canvasW = _canvas.Bounds.Width;
        double canvasH = _canvas.Bounds.Height;
        if (canvasW <= 0 || canvasH <= 0) return;

        double scaleX = canvasW / CameraGridManager.GridWidth;
        double scaleY = canvasH / CameraGridManager.GridHeight;

        for (int i = 0; i < _tiles.Count; i++)
        {
            var tile = _tiles[i];
            var r    = i < rects.Count ? rects[i] : rects[^1];

            double x = r.X * scaleX;
            double y = r.Y * scaleY;
            double w = r.Width  * scaleX;
            double h = r.Height * scaleY;

            Canvas.SetLeft(tile, x);
            Canvas.SetTop(tile,  y);
            tile.Width  = w;
            tile.Height = h;
        }
    }

    private void UpdateTitle()
    {
        _titleText.Text = $"◢ CAMERA GRID  [{_tiles.Count} camera{(_tiles.Count == 1 ? "" : "s")}]";
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Tile events
    // ──────────────────────────────────────────────────────────────────────────

    private void Tile_OnCloseRequested(object? sender, string uniqueId)
    {
        RemoveCamera(uniqueId);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Title-bar drag (moves the OS window)
    // ──────────────────────────────────────────────────────────────────────────

    private void TitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        _isDragging         = true;
        _dragStartPointer   = e.GetPosition(this);
        _dragStartWindowPos = Position;
        // Cache screen scaling once on press to avoid per-move lookup.
        var screen = Screens.ScreenFromWindow(this);
        _dragScale = screen?.Scaling ?? 1.0;
        e.Pointer.Capture((IInputElement?)sender);
        e.Handled = true;
    }

    private void TitleBar_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging) return;

        var current = e.GetPosition(this);
        var delta   = current - _dragStartPointer;

        var newX = _dragStartWindowPos.X + (int)Math.Round(delta.X * _dragScale);
        var newY = _dragStartWindowPos.Y + (int)Math.Round(delta.Y * _dragScale);

        Position = new PixelPoint(newX, newY);
        e.Handled = true;
    }

    private void TitleBar_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void ResizeGrip_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_isFullscreen || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        BeginResizeDrag(WindowEdge.SouthEast, e);
        e.Handled = true;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Fullscreen
    // ──────────────────────────────────────────────────────────────────────────

    private void FullscreenButton_OnClick(object? sender, RoutedEventArgs e) => ToggleFullscreen();
    private void CloseButton_OnClick(object? sender, RoutedEventArgs e)      => CloseAll();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.S
            && e.KeyModifiers.HasFlag(KeyModifiers.Control)
            && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            CaptureShortcutPressed?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F11) { ToggleFullscreen(); e.Handled = true; }
        if (e.Key == Key.Escape && _isFullscreen) { ExitFullscreen(); e.Handled = true; }
    }

    private void ToggleFullscreen()
    {
        if (_isFullscreen) ExitFullscreen();
        else               EnterFullscreen();
    }

    private void EnterFullscreen()
    {
        _restorePos = Position;
        _restoreW   = Width;
        _restoreH   = Height;

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen == null) return;

        double scale = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
        double sw    = screen.Bounds.Width  / scale;
        double sh    = screen.Bounds.Height / scale;

        Width    = sw;
        Height   = sh;
        Position = screen.Bounds.Position;

        _isFullscreen = true;
        UpdateResizeGripState();
        // Reflow fires automatically via SizeChanged.
    }

    private void ExitFullscreen()
    {
        Width    = _restoreW;
        Height   = _restoreH;
        Position = _restorePos;

        _isFullscreen = false;
        UpdateResizeGripState();
        // Reflow fires automatically via SizeChanged.
    }

    private void UpdateResizeGripState()
    {
        _resizeGrip.IsVisible = !_isFullscreen;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Cleanup
    // ──────────────────────────────────────────────────────────────────────────

    protected override void OnClosed(EventArgs e)
    {
        SizeChanged -= OnSizeChanged;

        foreach (var tile in _tiles)
            tile.Teardown();

        _tiles.Clear();
        base.OnClosed(e);
    }
}

// ══════════════════════════════════════════════════════════════════════════════
// CameraGridTile — one camera cell inside the canvas
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// A self-contained camera tile: VideoView + title bar with close button.
/// Sized and positioned entirely by <see cref="CameraGridWindow"/>.
/// </summary>
internal sealed class CameraGridTile : Panel
{
    public event EventHandler<string>? CloseRequested;

    public CameraItem Camera { get; }

    private readonly VlcPlayerService     _vlcService;
    private readonly MpvPlayerService     _mpvService;
    private readonly StreamHealthMonitor? _healthMonitor;
    private readonly Action<CameraHealthState>? _openHealthPopup;
    private readonly VideoView?           _videoView;
    private readonly MpvVideoHost?        _mpvHost;
    private readonly TextBlock            _title;
    private readonly Button?              _healthButton;
    private readonly Border               _errorOverlay;
    private readonly TextBlock            _errorText;
    private readonly Border               _loadingOverlay;
    private readonly bool                 _usesMpv;
    private MediaPlayer?                  _player;
    private nint                          _mpvWindowId;   // last window handle used to start mpv; stable for tile lifetime
    private int                           _playbackVersion;
    private CancellationTokenSource?      _playbackCts;
    private EventHandler<EventArgs>?      _onFirstPlaying;
    private long                          _playbackStartedTicks;
    private StreamHealthProbeBase?        _healthProbe;
    private CameraHealthState?            _healthState;
    private bool                          _hasStartedOnce;

    private const double TileHeaderHeight = 24;

    public CameraGridTile(
        CameraItem camera,
        VlcPlayerService vlcService,
        MpvPlayerService mpvService,
        StreamHealthMonitor? healthMonitor = null,
        Action<CameraHealthState>? openHealthPopup = null)
    {
        Camera           = camera;
        _vlcService      = vlcService;
        _mpvService      = mpvService;
        _healthMonitor   = healthMonitor;
        _openHealthPopup = openHealthPopup;
        _usesMpv         = camera.Kind == CameraKind.Youtube;

        Background = ThemeService.GetBrush("BrushPopoutWindowBackground");
        ClipToBounds = true;

        // ── Header bar ──
        var header = new Border
        {
            Background = ThemeService.GetBrush("BrushPopoutTileHeaderBackground"),
            Height     = TileHeaderHeight,
            VerticalAlignment = VerticalAlignment.Top
        };

        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };

        _title = new TextBlock
        {
            FontSize          = 10,
            Foreground        = ThemeService.GetBrush("BrushPopoutTitleForeground"),
            FontWeight        = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(6, 0),
            Text              = camera.DisplayLabel,
            TextTrimming      = TextTrimming.CharacterEllipsis
        };

        // Health gear: only created when a monitor is present. Visibility tracks
        // the global ShowBadges toggle; click opens the popup.
        if (_healthMonitor is not null)
        {
            _healthButton = new Button
            {
                Content           = "⚙",
                FontSize          = 11,
                Width             = 22,
                Height            = TileHeaderHeight,
                Background        = Brushes.Transparent,
                Foreground        = HealthBrushes.ForClassification(HealthClassification.Healthy),
                BorderThickness   = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor            = new Cursor(StandardCursorType.Hand),
                IsVisible         = _healthMonitor.ShowBadges
            };
            ToolTip.SetTip(_healthButton, "Stream health");
            _healthButton.Click += (_, _) =>
            {
                if (_healthState is not null)
                    _openHealthPopup?.Invoke(_healthState);
            };
            _healthMonitor.ShowBadgesChanged += OnShowBadgesChanged;
        }

        var closeBtn = new Button
        {
            Content           = "✕",
            FontSize          = 10,
            Width             = 24,
            Height            = TileHeaderHeight,
            Background        = Brushes.Transparent,
            Foreground        = ThemeService.GetBrush("BrushPopoutCloseForeground"),
            BorderThickness   = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor            = new Cursor(StandardCursorType.Hand)
        };
        closeBtn.Click += (_, _) => CloseRequested?.Invoke(this, Camera.UniqueId);

        Grid.SetColumn(_title,    0);
        Grid.SetColumn(closeBtn,  2);
        headerGrid.Children.Add(_title);
        if (_healthButton is not null)
        {
            Grid.SetColumn(_healthButton, 1);
            headerGrid.Children.Add(_healthButton);
        }
        headerGrid.Children.Add(closeBtn);
        header.Child = headerGrid;

        // ── Video surface (mpv for YouTube, VLC for everything else) ──
        if (_usesMpv)
        {
            _mpvHost = new MpvVideoHost
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment   = VerticalAlignment.Stretch,
                ClipToBounds        = true
            };
        }
        else
        {
            _videoView = new VideoView
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment   = VerticalAlignment.Stretch,
                ClipToBounds        = true
            };
        }

        // ── Error overlay ──
        _errorText = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            TextAlignment       = TextAlignment.Center,
            TextWrapping        = TextWrapping.Wrap,
            Foreground          = ThemeService.GetBrush("BrushTextWhite"),
            FontSize            = 11,
            Margin              = new Thickness(8)
        };
        _errorOverlay = new Border
        {
            Background = ThemeService.GetBrush("BrushPopoutOverlayBackground"),
            IsVisible  = false,
            Child      = _errorText
        };

        // ── Loading placeholder (visible until first frame) ──
        _loadingOverlay = new Border
        {
            Background = ThemeService.GetBrush("BrushPopoutOverlayBackground"),
            IsVisible  = true,
            Child      = new TextBlock
            {
                Text                = "Loading…",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                Foreground          = ThemeService.GetBrush("BrushTextWhite"),
                FontSize            = 11
            }
        };

        Children.Add((Control?)_videoView ?? _mpvHost!);
        Children.Add(_loadingOverlay);
        Children.Add(_errorOverlay);
        Children.Add(header);

        // Start playback once attached to the visual tree; unsubscribe immediately
        // so the lambda does not keep a reference to the tile alive.
        EventHandler<VisualTreeAttachmentEventArgs>? onAttached = null;
        onAttached = (_, _) =>
        {
            AttachedToVisualTree -= onAttached;
            RestartPlayback();
        };
        AttachedToVisualTree += onAttached;
    }

    private Control VideoSurface => (Control?)_videoView ?? _mpvHost!;

    // Lay out children: video surface fills the tile below the header,
    // header sits on top, error overlay fills the video area.
    protected override Size ArrangeOverride(Size finalSize)
    {
        var videoRect = new Rect(0, TileHeaderHeight, finalSize.Width, Math.Max(0, finalSize.Height - TileHeaderHeight));
        VideoSurface.Arrange(videoRect);
        _errorOverlay.Arrange(videoRect);
        _loadingOverlay.Arrange(videoRect);

        foreach (Control child in Children)
        {
            if (!ReferenceEquals(child, VideoSurface)
                && !ReferenceEquals(child, _errorOverlay)
                && !ReferenceEquals(child, _loadingOverlay))
            {
                child.Arrange(new Rect(0, 0, finalSize.Width, TileHeaderHeight));
            }
        }

        return finalSize;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Canvas passes Size.Infinity to children during measure.
        // Clamp to a finite size so Avalonia's layout engine doesn't reject the result.
        var finite = new Size(
            double.IsInfinity(availableSize.Width)  ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);

        foreach (Control child in Children)
            child.Measure(finite);

        return finite;
    }

    private async Task StartPlaybackAsync(int playbackVersion, CancellationToken ct)
    {
        // Bound concurrent tile starts so a 9-tile grid doesn't open 9 sockets at once.
        IDisposable? gateSlot = null;
        try
        {
            gateSlot = await TileLoadGate.AcquireAsync(Camera.UniqueId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (playbackVersion != _playbackVersion || ct.IsCancellationRequested) return;

            _playbackStartedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            TileTiming.Event(Camera.UniqueId, "playback.begin",
                $"backend={(_usesMpv ? "mpv" : "vlc")}");

            if (_usesMpv)
            {
                await StartMpvAsync(playbackVersion, ct).ConfigureAwait(false);
            }
            else
            {
                await StartVlcAsync(playbackVersion, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            gateSlot?.Dispose();
        }
    }

    private async Task StartMpvAsync(int playbackVersion, CancellationToken ct)
    {
        var url = Camera.YoutubeUrl ?? Camera.StreamUrl ?? string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            ShowError("No YouTube URL configured.");
            return;
        }

        // The NativeControlHost creates its X11 window asynchronously after
        // attach. Poll the handle instead of a fixed sleep so we proceed the
        // instant it's ready, and bail cleanly on cancellation. 80 × 25ms = 2s
        // upper bound — under load the handle can take noticeably longer than
        // the first few polls to materialize.
        nint windowId = 0;
        using (TileTiming.Begin(Camera.UniqueId, "mpv.window.wait"))
        {
            for (int i = 0; i < 80 && !ct.IsCancellationRequested; i++)
            {
                windowId = await Dispatcher.UIThread.InvokeAsync(() => _mpvHost!.NativeWindowId);
                if (windowId != 0) break;
                try { await Task.Delay(25, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        if (playbackVersion != _playbackVersion || ct.IsCancellationRequested) return;
        if (windowId == 0)
        {
            ShowError("Native window not ready.");
            return;
        }

        // LoadUrl reuses an existing process via IPC `loadfile` when possible.
        _mpvWindowId = windowId;
        using (TileTiming.Begin(Camera.UniqueId, "mpv.loadurl"))
        {
            _mpvService.LoadUrl(Camera.UniqueId, windowId, url);
        }
        HideError();
        // Subprocess mpv has no Playing-event equivalent. Hide the placeholder
        // optimistically; if the process dies the health probe will surface it.
        HideLoading();
    }

    private async Task StartVlcAsync(int playbackVersion, CancellationToken ct)
    {
        string? resolvedUrl = Camera.StreamUrl ?? Camera.TxDotUrl;
        if (string.IsNullOrWhiteSpace(resolvedUrl))
        {
            ShowError("Stream unavailable.");
            return;
        }

        var startedAt = _playbackStartedTicks;
        EventHandler<EventArgs> onPlaying = (_, _) =>
        {
            var elapsed = (long)System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            TileTiming.Mark(Camera.UniqueId, "vlc.first.playing", elapsed);
            Dispatcher.UIThread.Post(HideLoading);
        };

        // Acquire or reuse the MediaPlayer on the UI thread so this races safely with
        // Teardown/StopPlayback. On refresh _player is already set — reuse it without
        // touching VideoView.MediaPlayer. The native window handle is never invalidated,
        // so VLC cannot fall back to spawning a standalone OS window (the pop-out bug).
        // On first start (or after Teardown) _player is null — create and attach once.
        MediaPlayer player = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var oldHandler = _onFirstPlaying;
            _onFirstPlaying = null;
            if (oldHandler is not null && _player is not null)
                try { _player.Playing -= oldHandler; } catch { }

            if (_player is not null)
            {
                _onFirstPlaying = onPlaying;
                _player.Playing += onPlaying;
                return _player;
            }

            MediaPlayer p;
            using (TileTiming.Begin(Camera.UniqueId, "vlc.acquire"))
                p = _vlcService.CreateMutedPlayer(disableVideoTitle: true);
            _onFirstPlaying = onPlaying;
            p.Playing += onPlaying;
            _player = p;
            _videoView!.MediaPlayer = p;
            return p;
        });

        if (playbackVersion != _playbackVersion || ct.IsCancellationRequested) return;

        try
        {
            await _vlcService
                .PlayStreamAsync(player, resolvedUrl, Camera.UniqueId, ct: ct)
                .ConfigureAwait(false);
            HideError();
        }
        catch (OperationCanceledException)
        {
            // Player stays attached to VideoView — it will be reused by the next refresh
            // or released by Teardown via StopPlayback(). Only clean up the event handler
            // to prevent a stale HideLoading call after this attempt is abandoned.
            try { player.Playing -= onPlaying; } catch { }
            _onFirstPlaying = null;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    public void RestartPlayback()
    {
        // Bump version *before* swapping the CTS so an in-flight attempt that
        // hasn't yet observed cancellation still fails its version check.
        _playbackVersion++;

        // Cancel any in-flight Parse/loadfile from the previous attempt.
        var oldCts = Interlocked.Exchange(ref _playbackCts, new CancellationTokenSource());
        try { oldCts?.Cancel(); oldCts?.Dispose(); } catch { }

        // MPV: keep the process alive; LoadUrl reuses it via IPC.
        // VLC: keep the existing MediaPlayer attached to the VideoView.
        //      StartVlcAsync reuses the same player instance and changes only the media,
        //      so the native window handle is never invalidated and VLC cannot spawn a
        //      standalone OS window (the "pop-out" crash on refresh).
        //      StopPlayback is called only from Teardown() when the tile is removed.

        HideError();
        ShowLoading();

        var token = _playbackCts!.Token;
        var version = _playbackVersion;
        _ = Task.Run(() => StartPlaybackAsync(version, token), token);

        if (_hasStartedOnce)
        {
            // First call comes from the AttachedToVisualTree handler — that's
            // the initial start, not a reconnect. Subsequent calls are user-
            // or grid-driven restarts and should count as a reconnect.
            _healthProbe?.NotifyReconnect("Tile playback restarted");
        }
        else
        {
            _hasStartedOnce = true;
            EnsureHealthRegistration();
        }
    }

    private void EnsureHealthRegistration()
    {
        if (_healthMonitor is null || _healthProbe is not null) return;

        StreamHealthProbeBase probe = _usesMpv
            ? new MpvStreamHealthProbe(() => _mpvService.IsRunning(Camera.UniqueId, _mpvWindowId), Camera.YoutubeUrl ?? Camera.StreamUrl)
            : new VlcStreamHealthProbe(() => _player, Camera.StreamUrl ?? Camera.TxDotUrl);

        _healthProbe = probe;
        _healthState = _healthMonitor.Register(Camera.UniqueId, Camera.DisplayLabel, probe);
        _healthState.PropertyChanged += OnHealthStateChanged;
        UpdateHealthVisuals();
    }

    private void OnShowBadgesChanged(object? sender, bool show)
    {
        if (_healthButton is null) return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => _healthButton.IsVisible = show);
        }
        else
        {
            _healthButton.IsVisible = show;
        }
    }

    private void OnHealthStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(UpdateHealthVisuals);
        }
        else
        {
            UpdateHealthVisuals();
        }
    }

    private void UpdateHealthVisuals()
    {
        if (_healthButton is null || _healthState is null) return;
        _healthButton.Foreground = HealthBrushes.ForClassification(_healthState.Classification);
        var summary = HealthBrushes.SummaryFor(_healthState.LatestSample, _healthProbe?.ReconnectCount ?? 0);
        ToolTip.SetTip(_healthButton, $"{_healthState.Classification}\n{summary}");
    }

    private void ShowError(string msg)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ShowError(msg));
            return;
        }

        _errorText.Text = msg;
        _errorOverlay.IsVisible = true;
        _loadingOverlay.IsVisible = false;
    }

    private void HideError()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(HideError);
            return;
        }

        _errorOverlay.IsVisible = false;
    }

    private void ShowLoading()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(ShowLoading);
            return;
        }
        _loadingOverlay.IsVisible = true;
    }

    private void HideLoading()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(HideLoading);
            return;
        }
        _loadingOverlay.IsVisible = false;
    }

    private void StopPlayback()
    {
        if (_usesMpv)
        {
            _mpvService.Stop(Camera.UniqueId, _mpvWindowId);
        }
        else
        {
            var player = _player;
            _player = null;
            if (player == null) return;

            // Detach the first-Playing handler before recycling.
            var handler = _onFirstPlaying;
            _onFirstPlaying = null;
            if (handler is not null)
            {
                try { player.Playing -= handler; } catch { }
            }

            // Stop before detaching: if the player is still rendering when
            // MediaPlayer is set to null, VLC's video output loses its native
            // window handle and falls back to spawning a standalone OS window
            // (the "pop-out" crash on refresh). Release() calls Stop() again
            // internally, but that is harmless on an already-stopped player.
            try { player.Stop(); } catch { }

            if (_videoView is not null)
            {
                _videoView.MediaPlayer = null;
            }
            _vlcService.Release(player);
        }
    }

    /// <summary>Stops playback and cleans up the player/process.</summary>
    public void Teardown()
    {
        _playbackVersion++;

        // Cancel any in-flight startup work so it doesn't finish into a torn-down tile.
        var cts = Interlocked.Exchange(ref _playbackCts, null);
        try { cts?.Cancel(); cts?.Dispose(); } catch { }

        StopPlayback();

        if (_healthMonitor is not null)
        {
            _healthMonitor.ShowBadgesChanged -= OnShowBadgesChanged;
            _healthMonitor.Unregister(Camera.UniqueId);
        }
        if (_healthState is not null)
        {
            _healthState.PropertyChanged -= OnHealthStateChanged;
            _healthState = null;
        }
        _healthProbe = null;
    }
}
