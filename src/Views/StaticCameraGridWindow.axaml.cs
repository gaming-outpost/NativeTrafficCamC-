using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CoastalCommandCenter.Models;
using CoastalCommandCenter.Models.Health;
using CoastalCommandCenter.Services;
using CoastalCommandCenter.Services.Health;

namespace CoastalCommandCenter.Views;

public partial class StaticCameraGridWindow : Window
{
    private const int DefaultRefreshIntervalSeconds = 180;
    private const double TitleBarHeight = 24;

    private readonly StaticSnapshotService _snapshotService;
    private readonly Canvas _canvas;
    private readonly TextBlock _titleText;
    private readonly TextBlock _statusText;
    private readonly TextBlock _intervalText;
    private readonly CheckBox _autoRefreshToggle;
    private readonly Border _resizeGrip;
    private readonly List<StaticCameraTile> _tiles = [];

    private DispatcherTimer? _autoRefreshTimer;
    private CancellationTokenSource? _refreshCts;
    private bool _refreshLoopActive;
    private bool _refreshPending;
    private bool _isDragging;
    private Point _dragStartPointer;
    private PixelPoint _dragStartWindowPos;
    private double _dragScale;
    private bool _isFullscreen;
    private PixelPoint _restorePos;
    private double _restoreW;
    private double _restoreH;
    private DateTimeOffset? _lastSuccessfulRefresh;
    private int _refreshIntervalSeconds = DefaultRefreshIntervalSeconds;
    private string _currentGroupName = "Static Views";

    public string? CurrentGroupId { get; private set; }
    public int TileCount => _tiles.Count;

    public StaticCameraGridWindow()
        : this(new StaticSnapshotService())
    {
    }

    public StaticCameraGridWindow(StaticSnapshotService snapshotService)
    {
        _snapshotService = snapshotService;

        InitializeComponent();

        _canvas = this.FindControl<Canvas>("CameraCanvas")
            ?? throw new InvalidOperationException("CameraCanvas not found.");
        _titleText = this.FindControl<TextBlock>("GridTitleText")
            ?? throw new InvalidOperationException("GridTitleText not found.");
        _statusText = this.FindControl<TextBlock>("RefreshStatusText")
            ?? throw new InvalidOperationException("RefreshStatusText not found.");
        _intervalText = this.FindControl<TextBlock>("RefreshIntervalText")
            ?? throw new InvalidOperationException("RefreshIntervalText not found.");
        _autoRefreshToggle = this.FindControl<CheckBox>("AutoRefreshToggle")
            ?? throw new InvalidOperationException("AutoRefreshToggle not found.");
        _resizeGrip = this.FindControl<Border>("ResizeGrip")
            ?? throw new InvalidOperationException("ResizeGrip not found.");

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        SizeChanged += OnSizeChanged;

        SetRefreshInterval(DefaultRefreshIntervalSeconds);
        ApplyAutoRefreshSetting();
        UpdateTitle();
        UpdateStatusText("Ready.");
        UpdateResizeGripState();
    }

    public void ApplyThemeMode(AppThemeMode mode)
    {
        // Theme is applied globally via ThemeService brush mutations.
    }

    public void LoadGroup(StaticCameraGroupDefinition group, bool refreshNow = true)
    {
        ArgumentNullException.ThrowIfNull(group);

        CurrentGroupId = group.Id;
        _currentGroupName = string.IsNullOrWhiteSpace(group.DisplayName) ? group.Id : group.DisplayName;
        Title = $"Static Views - {_currentGroupName}";
        SetRefreshInterval(group.EffectiveRefreshIntervalSeconds);

        RebuildTiles(group.Cameras);
        UpdateTitle();
        UpdateStatusText($"Loaded {_tiles.Count} static camera{(_tiles.Count == 1 ? "" : "s")}.");

        if (refreshNow)
        {
            RequestRefresh();
        }
    }

    public void RequestRefresh()
    {
        if (_tiles.Count == 0)
        {
            UpdateStatusText("No static cameras configured.");
            return;
        }

        _refreshPending = true;
        if (_refreshLoopActive)
        {
            return;
        }

        _ = RunRefreshLoopAsync();
    }

    private async Task RunRefreshLoopAsync()
    {
        if (_refreshLoopActive)
        {
            return;
        }

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
            UpdateStatusText();
        }
    }

    private async Task RefreshTilesAsync()
    {
        CancelRefresh();

        var refreshCts = new CancellationTokenSource();
        _refreshCts = refreshCts;

        foreach (var tile in _tiles)
        {
            tile.BeginRefresh();
        }

        UpdateStatusText($"Refreshing {_tiles.Count} camera{(_tiles.Count == 1 ? "" : "s")}...");

        try
        {
            var results = await Task.WhenAll(_tiles.Select(tile => tile.RefreshAsync(_snapshotService, refreshCts.Token)));
            if (!ReferenceEquals(_refreshCts, refreshCts))
            {
                return;
            }

            var successCount = results.Count(success => success);
            if (successCount > 0)
            {
                _lastSuccessfulRefresh = DateTimeOffset.Now;
            }

            UpdateStatusText($"Updated {successCount}/{_tiles.Count} cameras at {FormatTimestamp(_lastSuccessfulRefresh)}.");
        }
        catch (OperationCanceledException)
        {
            if (!ReferenceEquals(_refreshCts, refreshCts))
            {
                return;
            }

            UpdateStatusText("Refresh canceled.");
        }
        finally
        {
            if (ReferenceEquals(_refreshCts, refreshCts))
            {
                _refreshCts = null;
            }

            refreshCts.Dispose();
        }
    }

    private void RebuildTiles(IReadOnlyList<StaticCameraDefinition> cameras)
    {
        foreach (var tile in _tiles)
        {
            tile.Dispose();
        }

        _tiles.Clear();
        _canvas.Children.Clear();

        foreach (var camera in cameras)
        {
            var tile = new StaticCameraTile(camera);
            _tiles.Add(tile);
            _canvas.Children.Add(tile);
        }

        Reflow();
    }

    private void SetRefreshInterval(int refreshIntervalSeconds)
    {
        _refreshIntervalSeconds = refreshIntervalSeconds > 0
            ? refreshIntervalSeconds
            : DefaultRefreshIntervalSeconds;

        _autoRefreshTimer ??= new DispatcherTimer();
        _autoRefreshTimer.Interval = TimeSpan.FromSeconds(_refreshIntervalSeconds);
        _autoRefreshTimer.Tick -= OnAutoRefreshTimerTick;
        _autoRefreshTimer.Tick += OnAutoRefreshTimerTick;

        UpdateRefreshIntervalText();
    }

    private void ApplyAutoRefreshSetting()
    {
        if (_autoRefreshTimer == null)
        {
            return;
        }

        if (_autoRefreshToggle.IsChecked == true)
        {
            if (!_autoRefreshTimer.IsEnabled)
            {
                _autoRefreshTimer.Start();
            }
        }
        else
        {
            _autoRefreshTimer.Stop();
        }

        UpdateRefreshIntervalText();
    }

    private void UpdateRefreshIntervalText()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(UpdateRefreshIntervalText);
            return;
        }

        _intervalText.Text = _autoRefreshToggle.IsChecked == true
            ? $"AUTO {_refreshIntervalSeconds}s"
            : $"AUTO OFF ({_refreshIntervalSeconds}s)";
    }

    private void UpdateTitle()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(UpdateTitle);
            return;
        }

        _titleText.Text = $"STATIC VIEWS [{_currentGroupName} | {_tiles.Count} camera{(_tiles.Count == 1 ? "" : "s")}]";
    }

    private void UpdateStatusText(string? message = null)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => UpdateStatusText(message));
            return;
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            _statusText.Text = message;
            return;
        }

        if (_refreshLoopActive)
        {
            _statusText.Text = $"Refreshing {_tiles.Count} camera{(_tiles.Count == 1 ? "" : "s")}...";
            return;
        }

        var suffix = _lastSuccessfulRefresh.HasValue
            ? $"Last success: {FormatTimestamp(_lastSuccessfulRefresh)}"
            : "No successful refresh yet.";

        _statusText.Text = _autoRefreshToggle.IsChecked == true
            ? $"Ready. {suffix}"
            : $"Auto refresh paused. {suffix}";
    }

    private void OnAutoRefreshTimerTick(object? sender, EventArgs e)
    {
        RequestRefresh();
    }

    private static string FormatTimestamp(DateTimeOffset? timestamp)
    {
        return timestamp?.ToString("h:mm:ss tt") ?? "--";
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        Reflow();
    }

    private void Reflow()
    {
        var rects = CameraGridManager.CalculateLayout(_tiles.Count);

        double canvasW = _canvas.Bounds.Width;
        double canvasH = _canvas.Bounds.Height;
        if (canvasW <= 0 || canvasH <= 0)
        {
            return;
        }

        double scaleX = canvasW / CameraGridManager.GridWidth;
        double scaleY = canvasH / CameraGridManager.GridHeight;

        for (int i = 0; i < _tiles.Count; i++)
        {
            var tile = _tiles[i];
            var rect = i < rects.Count ? rects[i] : rects[^1];

            double x = rect.X * scaleX;
            double y = rect.Y * scaleY;
            double w = rect.Width * scaleX;
            double h = rect.Height * scaleY;

            Canvas.SetLeft(tile, x);
            Canvas.SetTop(tile, y);
            tile.Width = w;
            tile.Height = h;
        }
    }

    private void RefreshButton_OnClick(object? sender, RoutedEventArgs e)
    {
        RequestRefresh();
    }

    private void AutoRefreshToggle_OnChanged(object? sender, RoutedEventArgs e)
    {
        ApplyAutoRefreshSetting();
        UpdateStatusText();
    }

    private void FullscreenButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ToggleFullscreen();
    }

    private void CloseButton_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            RequestRefresh();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _isFullscreen)
        {
            ExitFullscreen();
            e.Handled = true;
        }
    }

    private void TitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isDragging = true;
        _dragStartPointer = e.GetPosition(this);
        _dragStartWindowPos = Position;
        var screen = Screens.ScreenFromWindow(this);
        _dragScale = screen?.Scaling ?? 1.0;
        e.Pointer.Capture((IInputElement?)sender);
        e.Handled = true;
    }

    private void TitleBar_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        var current = e.GetPosition(this);
        var delta = current - _dragStartPointer;

        Position = new PixelPoint(
            _dragStartWindowPos.X + (int)Math.Round(delta.X * _dragScale),
            _dragStartWindowPos.Y + (int)Math.Round(delta.Y * _dragScale));
        e.Handled = true;
    }

    private void TitleBar_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
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

    private void ToggleFullscreen()
    {
        if (_isFullscreen)
        {
            ExitFullscreen();
        }
        else
        {
            EnterFullscreen();
        }
    }

    private void EnterFullscreen()
    {
        _restorePos = Position;
        _restoreW = Width;
        _restoreH = Height;

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen == null)
        {
            return;
        }

        double scale = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
        Width = screen.Bounds.Width / scale;
        Height = screen.Bounds.Height / scale;
        Position = screen.Bounds.Position;
        _isFullscreen = true;
        UpdateResizeGripState();
    }

    private void ExitFullscreen()
    {
        Width = _restoreW;
        Height = _restoreH;
        Position = _restorePos;
        _isFullscreen = false;
        UpdateResizeGripState();
    }

    private void UpdateResizeGripState()
    {
        _resizeGrip.IsVisible = !_isFullscreen;
    }

    protected override void OnClosed(EventArgs e)
    {
        CancelRefresh();
        if (_autoRefreshTimer != null)
        {
            _autoRefreshTimer.Stop();
            _autoRefreshTimer.Tick -= OnAutoRefreshTimerTick;
        }

        SizeChanged -= OnSizeChanged;

        foreach (var tile in _tiles)
        {
            tile.Dispose();
        }

        _tiles.Clear();
        base.OnClosed(e);
    }

    private void CancelRefresh()
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = null;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}

internal sealed class StaticCameraTile : Panel, IDisposable
{
    private const double TileHeaderHeight = 24;
    private const double TileMetadataHeight = 36;
    private const double TileStatusHeight = 22;

    private readonly Image _image;
    private readonly TextBlock _lastUpdatedText;
    private readonly Border _metadataFooter;
    private readonly Border _centerOverlay;
    private readonly TextBlock _centerOverlayText;
    private readonly Border _statusBanner;
    private readonly TextBlock _statusBannerText;
    private readonly Button? _healthButton;
    private readonly StreamHealthMonitor? _healthMonitor;
    private readonly Action<CameraHealthState>? _openHealthPopup;
    private readonly StaticSnapshotHealthProbe? _healthProbe;
    private readonly CameraHealthState? _healthState;
    private Bitmap? _bitmap;
    private string? _lastError;
    private bool _isLoading;
    private bool _disposed;

    public StaticCameraDefinition Camera { get; }

    public StaticCameraTile(StaticCameraDefinition camera, bool isEmbedded = false)
        : this(camera, null, null, isEmbedded)
    {
    }

    public StaticCameraTile(
        StaticCameraDefinition camera,
        StreamHealthMonitor? healthMonitor,
        Action<CameraHealthState>? openHealthPopup,
        bool isEmbedded = false)
    {
        Camera = camera;
        _healthMonitor = healthMonitor;
        _openHealthPopup = openHealthPopup;

        Background = isEmbedded
            ? ThemeService.GetBrush("BrushEmbeddedGridBackground")
            : ThemeService.GetBrush("BrushPopoutWindowBackground");
        ClipToBounds = true;

        var header = new Border
        {
            Background = isEmbedded
                ? ThemeService.GetBrush("BrushPanelBg")
                : ThemeService.GetBrush("BrushPopoutTileHeaderBackground"),
            Height = TileHeaderHeight,
            VerticalAlignment = VerticalAlignment.Top
        };

        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };

        var title = new TextBlock
        {
            FontSize = 10,
            Foreground = isEmbedded
                ? ThemeService.GetBrush("BrushText")
                : ThemeService.GetBrush("BrushPopoutTitleForeground"),
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0),
            Text = camera.Name,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        _lastUpdatedText = new TextBlock
        {
            FontSize = 9,
            Foreground = isEmbedded
                ? ThemeService.GetBrush("BrushTextDim")
                : ThemeService.GetBrush("BrushTextSubtle"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0),
            Text = "Updated --"
        };

        if (_healthMonitor is not null)
        {
            _healthProbe = new StaticSnapshotHealthProbe();
            _healthState = _healthMonitor.Register(camera.StableKey, camera.Name, _healthProbe);

            _healthButton = new Button
            {
                Content = "⚙",
                FontSize = 11,
                Width = 22,
                Height = TileHeaderHeight,
                Background = Brushes.Transparent,
                Foreground = HealthBrushes.ForClassification(HealthClassification.Healthy),
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand),
                IsVisible = _healthMonitor.ShowBadges
            };
            ToolTip.SetTip(_healthButton, "Stream health");
            _healthButton.Click += (_, _) =>
            {
                if (_healthState is not null)
                    _openHealthPopup?.Invoke(_healthState);
            };
            _healthMonitor.ShowBadgesChanged += OnShowBadgesChanged;
            _healthState.PropertyChanged += OnHealthStateChanged;
        }

        Grid.SetColumn(title, 0);
        Grid.SetColumn(_lastUpdatedText, 2);
        headerGrid.Children.Add(title);
        if (_healthButton is not null)
        {
            Grid.SetColumn(_healthButton, 1);
            headerGrid.Children.Add(_healthButton);
        }
        headerGrid.Children.Add(_lastUpdatedText);
        header.Child = headerGrid;

        _image = new Image
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Stretch = Stretch.Uniform
        };

        var locationText = new TextBlock
        {
            Foreground = isEmbedded
                ? ThemeService.GetBrush("BrushTextDim")
                : ThemeService.GetBrush("BrushTextSubtle"),
            FontSize = 9,
            Text = camera.HasLocation ? camera.Location : "Location unavailable",
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };

        var footerStack = new StackPanel
        {
            Spacing = 3,
            Margin = new Thickness(6, 4, 6, 4)
        };
        footerStack.Children.Add(locationText);

        _metadataFooter = new Border
        {
            Background = isEmbedded
                ? ThemeService.GetBrush("BrushPanelBg")
                : ThemeService.GetBrush("BrushPopoutTileHeaderBackground"),
            Child = footerStack
        };

        _centerOverlayText = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeService.GetBrush("BrushTextWhite"),
            FontSize = 11,
            Margin = new Thickness(8)
        };

        _centerOverlay = new Border
        {
            Background = ThemeService.GetBrush("BrushPopoutOverlayBackground"),
            IsVisible = false,
            Child = _centerOverlayText
        };

        _statusBannerText = new TextBlock
        {
            Foreground = ThemeService.GetBrush("BrushTextWhite"),
            FontSize = 10,
            Margin = new Thickness(8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        _statusBanner = new Border
        {
            Background = ThemeService.GetBrush("BrushPopoutOverlayBackground"),
            Height = TileStatusHeight,
            IsVisible = false,
            Child = _statusBannerText
        };

        ToolTip.SetTip(this, BuildTooltip(camera));

        Children.Add(_image);
        Children.Add(_centerOverlay);
        Children.Add(_metadataFooter);
        Children.Add(_statusBanner);
        Children.Add(header);
    }

    public void BeginRefresh()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(BeginRefresh);
            return;
        }

        _isLoading = true;
        UpdateVisualState();
    }

    public async Task<bool> RefreshAsync(StaticSnapshotService snapshotService, CancellationToken ct)
    {
        if (_disposed)
        {
            return false;
        }

        BeginRefresh();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await snapshotService.LoadSnapshotAsync(Camera, ct);
            sw.Stop();
            long bytes = 0;
            try { bytes = result.Bitmap.PixelSize.Width * result.Bitmap.PixelSize.Height * 3L; } catch { /* best-effort */ }
            _healthProbe?.RecordFetch(sw.Elapsed.TotalMilliseconds, bytes, success: true);
            return await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed)
                {
                    result.Bitmap.Dispose();
                    return false;
                }

                var previous = _bitmap;
                _bitmap = result.Bitmap;
                _image.Source = _bitmap;
                previous?.Dispose();

                _lastUpdatedText.Text = $"Updated {result.RetrievedAt:h:mm:ss tt}";
                _lastError = null;
                _isLoading = false;
                UpdateVisualState();
                return true;
            });
        }
        catch (OperationCanceledException)
        {
            _isLoading = false;
            UpdateVisualState();
            return false;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _healthProbe?.RecordFetch(sw.Elapsed.TotalMilliseconds, 0, success: false, errorDetail: ex.Message);
            _lastError = string.IsNullOrWhiteSpace(ex.Message)
                ? "Snapshot unavailable."
                : ex.Message;
            _isLoading = false;
            UpdateVisualState();
            return false;
        }
    }

    private void OnShowBadgesChanged(object? sender, bool show)
    {
        if (_healthButton is null) return;
        if (!Dispatcher.UIThread.CheckAccess())
            Dispatcher.UIThread.Post(() => _healthButton.IsVisible = show);
        else
            _healthButton.IsVisible = show;
    }

    private void OnHealthStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
            Dispatcher.UIThread.Post(UpdateHealthVisuals);
        else
            UpdateHealthVisuals();
    }

    private void UpdateHealthVisuals()
    {
        if (_healthButton is null || _healthState is null) return;
        _healthButton.Foreground = HealthBrushes.ForClassification(_healthState.Classification);
        var summary = HealthBrushes.SummaryFor(_healthState.LatestSample, _healthProbe?.ReconnectCount ?? 0);
        ToolTip.SetTip(_healthButton, $"{_healthState.Classification}\n{summary}");
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var contentRect = new Rect(
            0,
            TileHeaderHeight,
            finalSize.Width,
            Math.Max(0, finalSize.Height - TileHeaderHeight - TileMetadataHeight));

        _image.Arrange(contentRect);
        _centerOverlay.Arrange(contentRect);
        _statusBanner.Arrange(new Rect(
            0,
            Math.Max(TileHeaderHeight, finalSize.Height - TileMetadataHeight - TileStatusHeight),
            finalSize.Width,
            TileStatusHeight));
        _metadataFooter.Arrange(new Rect(
            0,
            Math.Max(TileHeaderHeight, finalSize.Height - TileMetadataHeight),
            finalSize.Width,
            TileMetadataHeight));

        foreach (Control child in Children)
        {
            if (!ReferenceEquals(child, _image)
                && !ReferenceEquals(child, _centerOverlay)
                && !ReferenceEquals(child, _statusBanner)
                && !ReferenceEquals(child, _metadataFooter))
            {
                child.Arrange(new Rect(0, 0, finalSize.Width, TileHeaderHeight));
            }
        }

        return finalSize;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var finite = new Size(
            double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);

        foreach (Control child in Children)
        {
            child.Measure(finite);
        }

        return finite;
    }

    public void Dispose()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Dispose);
            return;
        }

        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _image.Source = null;
        _bitmap?.Dispose();
        _bitmap = null;

        if (_healthMonitor is not null)
        {
            _healthMonitor.ShowBadgesChanged -= OnShowBadgesChanged;
            _healthMonitor.Unregister(Camera.StableKey);
        }
        if (_healthState is not null)
        {
            _healthState.PropertyChanged -= OnHealthStateChanged;
        }
    }

    private void UpdateVisualState()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(UpdateVisualState);
            return;
        }

        if (_isLoading && _bitmap == null)
        {
            _centerOverlayText.Text = "Loading snapshot...";
            _centerOverlay.IsVisible = true;
        }
        else if (!string.IsNullOrWhiteSpace(_lastError) && _bitmap == null)
        {
            _centerOverlayText.Text = BuildCenteredStatusText(_lastError);
            _centerOverlay.IsVisible = true;
        }
        else
        {
            _centerOverlay.IsVisible = false;
        }

        if (_isLoading && _bitmap != null)
        {
            _statusBanner.Background = ThemeService.GetBrush("BrushPopoutOverlayBackground");
            _statusBannerText.Text = "Updating snapshot...";
            _statusBanner.IsVisible = true;
            return;
        }

        if (!string.IsNullOrWhiteSpace(_lastError))
        {
            _statusBanner.Background = ThemeService.GetBrush("BrushDangerSecondary");
            _statusBannerText.Text = _bitmap != null
                ? $"Stale image: {TrimMessage(_lastError)}"
                : TrimMessage(_lastError);
            _statusBanner.IsVisible = true;
            return;
        }

        _statusBanner.IsVisible = false;
        _statusBannerText.Text = string.Empty;
    }

    private static string BuildCenteredStatusText(string message)
    {
        return $"Snapshot unavailable.\n{TrimMessage(message)}";
    }

    private static string TrimMessage(string message)
    {
        const int maxLength = 72;
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Unknown error.";
        }

        return message.Length <= maxLength
            ? message
            : $"{message[..(maxLength - 3)]}...";
    }

    private static string BuildTooltip(StaticCameraDefinition camera)
    {
        var parts = new List<string> { camera.Name };

        if (camera.HasSubgroup)
        {
            parts.Add($"Subgroup: {camera.Subgroup}");
        }

        if (camera.HasLocation)
        {
            parts.Add($"Location: {camera.Location}");
        }

        if (camera.HasCoordinates)
        {
            parts.Add($"Coords: {camera.Latitude!.Value:0.0000}, {camera.Longitude!.Value:0.0000}");
        }

        return string.Join(Environment.NewLine, parts);
    }
}
