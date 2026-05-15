using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using CoastalCommandCenter.Models;
using CoastalCommandCenter.Models.Health;
using CoastalCommandCenter.Services;
using CoastalCommandCenter.Services.Health;
using CoastalCommandCenter.ViewModels;

namespace CoastalCommandCenter.Views;

public partial class MainWindow : Window
{
    private readonly VlcPlayerService _vlcService;
    private readonly MpvPlayerService _mpvService;
    private readonly StaticSnapshotService _staticSnapshotService;
    private readonly StreamCaptureService _captureService;
    private readonly StreamHealthMonitor _healthMonitor;
    private readonly Grid _mapHost;
    private readonly Border? _rightPanelGridHost;
    private readonly Border? _cameraPreviewHost;
    private readonly TextBlock? _mapStatusText;
    private readonly WindowNotificationManager _notificationManager;

    private NativeMenuItem? _viewFullscreenMenuItem;
    private NativeMenuItem? _viewCloseSnapshotMenuItem;
    private NativeMenuItem? _overlayAlprMenuItem;
    private NativeMenuItem? _settingsLightThemeMenuItem;
    private NativeMenuItem? _settingsDarkThemeMenuItem;

    // Single shared camera grid window — replaces per-camera popouts.
    private CameraGridWindow? _cameraGrid;
    private readonly Dictionary<string, StaticCameraGridWindow> _staticCameraGrids =
        new(StringComparer.OrdinalIgnoreCase);

    // Map and embedded grid views.
    private MapService? _mapService;
    private EmbeddedCameraGrid? _rightPanelGrid;
    private EmbeddedCameraGrid? _cameraPreviewGrid;
    private EmbeddedCameraGrid? _slideshowGrid;
    private EmbeddedStaticCameraGrid? _embeddedStaticGrid;
    private Border? _staticViewsHost;
    private Border? _slideshowGridHost;
    private TextBlock? _staticViewsStatusText;
    private Slider? _staticTileSizeSlider;
    private bool _slideshowSubscribed;

    private MainViewModel? _mainViewModel;
    private bool _isFullscreenActive;
    private bool _isFullscreenTransitionInProgress;
    private bool _isShuttingDown;
    private WindowState _restoreWindowState = WindowState.Normal;
    private SystemDecorations _restoreSystemDecorations = SystemDecorations.None;
    private PixelPoint _restorePosition;
    private double _restoreWidth;
    private double _restoreHeight;
    private PixelPoint _cameraGridOffset; // grid position relative to main window
    private bool _isMovingGridFromMain;  // prevents feedback loop during position sync
    private readonly Dictionary<string, PixelPoint> _staticCameraGridOffsets =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _staticGridsMovingFromMain =
        new(StringComparer.OrdinalIgnoreCase);
    private Task? _mapInitializationTask;


    public MainWindow() : this(new VlcPlayerService(), new MpvPlayerService(), new StaticSnapshotService(), new StreamHealthMonitor())
    {
    }

    public MainWindow(
        VlcPlayerService vlcService,
        MpvPlayerService mpvService,
        StaticSnapshotService staticSnapshotService)
        : this(vlcService, mpvService, staticSnapshotService, new StreamHealthMonitor())
    {
    }

    public MainWindow(
        VlcPlayerService vlcService,
        MpvPlayerService mpvService,
        StaticSnapshotService staticSnapshotService,
        StreamHealthMonitor healthMonitor)
    {
        _vlcService = vlcService;
        _mpvService = mpvService;
        _staticSnapshotService = staticSnapshotService;
        _healthMonitor = healthMonitor;
        _captureService = new StreamCaptureService(mpvService);

        InitializeComponent();

        _mapHost = this.FindControl<Grid>("MapHost")
            ?? throw new InvalidOperationException("MapHost control was not found.");
        _rightPanelGridHost = this.FindControl<Border>("RightPanelGridHost");
        _cameraPreviewHost = this.FindControl<Border>("CameraPreviewHost");
        _mapStatusText = this.FindControl<TextBlock>("MapStatusText");
        _staticViewsHost = this.FindControl<Border>("StaticViewsHost");
        _slideshowGridHost = this.FindControl<Border>("SlideshowGridHost");
        _staticViewsStatusText = this.FindControl<TextBlock>("StaticViewsStatusText");
        _staticTileSizeSlider = this.FindControl<Slider>("StaticTileSizeSlider");
        if (_staticTileSizeSlider != null)
            _staticTileSizeSlider.ValueChanged += OnStaticTileSizeChanged;
        UpdateMapPlaceholder("Map will initialize after the first window paint.", isVisible: true);

        var mainTabControl = this.FindControl<TabControl>("MainTabControl");
        if (mainTabControl != null)
            mainTabControl.SelectionChanged += OnMainTabSelectionChanged;

        _notificationManager = new WindowNotificationManager(this)
        {
            Position = NotificationPosition.TopRight,
            MaxItems = 3
        };

        DataContextChanged += OnDataContextChanged;
        Closing += OnClosing;
        KeyDown += OnKeyDown;
        PropertyChanged += OnWindowPropertyChanged;
        Activated += OnWindowActivated;
        PositionChanged += OnWindowPositionChanged;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        BindViewModelIfNeeded();

        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);

        var dataLoadTask = _mainViewModel?.LoadDataAsync() ?? Task.CompletedTask;
        var mapInitTask = EnsureMapInitializedAsync();

        await Task.WhenAll(dataLoadTask, mapInitTask);

        _mainViewModel?.InitializeRightPanelDiagnosticsOnViewLoad();
        SyncEmbeddedFeedGrid();
        SyncCameraPreview();

        // If Static Views tab is already selected at startup, init now that data is loaded.
        var mainTabs = this.FindControl<TabControl>("MainTabControl");
        if (mainTabs?.SelectedIndex == 2)
            EnsureStaticViewsGridInitialized();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        BindViewModelIfNeeded();
    }

    private void BindViewModelIfNeeded()
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        if (ReferenceEquals(_mainViewModel, vm))
        {
            return;
        }

        DetachViewModelEvents();
        _mainViewModel = vm;

        _mainViewModel.CameraPopoutRequested += OnViewModelCameraPopoutRequested;
        _mainViewModel.CameraRenameRequested += OnViewModelCameraRenameRequested;
        _mainViewModel.MapFlyToRequested += OnViewModelMapFlyToRequested;
        _mainViewModel.LiveFeedSelectionChanged += OnViewModelLiveFeedSelectionChanged;
        _mainViewModel.CaptureVisibleStreamsRequested += OnViewModelCaptureVisibleStreamsRequested;
        _mainViewModel.RefreshVisibleFeedsRequested += OnViewModelRefreshVisibleFeedsRequested;
        _mainViewModel.StopAllPlaybackRequested += OnViewModelStopAllPlaybackRequested;
        _mainViewModel.StaticCameraGroupRequested += OnViewModelStaticCameraGroupRequested;
        _mainViewModel.StaticCameraSelectionChanged += OnViewModelStaticCameraSelectionChanged;
        _mainViewModel.PropertyChanged += OnViewModelPropertyChanged;
        _mainViewModel.ShowStreamHealthChanged += OnViewModelShowStreamHealthChanged;
        _healthMonitor.ShowBadges = _mainViewModel.ShowStreamHealth;
        ApplyRequestedFullscreenMode(_mainViewModel.IsFullscreenMode);
        ApplyThemeMode(_mainViewModel.SelectedThemeMode);
        BuildStaticViewsMenu();
        BuildShellMenu();
        UpdateShellMenuState();

        PushMapState();
    }

    private void UpdateMapPlaceholder(string message, bool isVisible)
    {
        if (_mapStatusText == null)
        {
            return;
        }

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => UpdateMapPlaceholder(message, isVisible));
            return;
        }

        _mapStatusText.Text = message;
        _mapStatusText.IsVisible = isVisible;
    }

    private void EnsureRightPanelGridInitialized()
    {
        if (_rightPanelGrid != null)
        {
            return;
        }

        _rightPanelGrid = new EmbeddedCameraGrid(_vlcService, _mpvService, _healthMonitor, OpenHealthPopup);
        _rightPanelGrid.TileCloseRequested += RightPanelGrid_OnTileCloseRequested;
        _rightPanelGrid.KeyDown += OnKeyDown;

        if (_rightPanelGridHost != null)
        {
            _rightPanelGridHost.Child = _rightPanelGrid;
        }
    }


    public Task EnsureMapInitializedAsync()
    {
        _mapInitializationTask ??= EnsureMapInitializedCoreAsync();
        return _mapInitializationTask;
    }

    private Task EnsureMapInitializedCoreAsync()
    {
        if (_mapService != null)
        {
            return Task.CompletedTask;
        }

        UpdateMapPlaceholder("Loading map...", isVisible: true);

        _mapService = new MapService();
        _mapService.Initialize();
        _mapService.CameraMarkerClicked += OnMapCameraClicked;
        _mapService.FeedMarkerClicked += OnMapFeedClicked;
        _mapService.StaticGroupCameraMarkerClicked += OnMapStaticGroupCameraClicked;
        _mapService.MapControl.KeyDown += OnKeyDown;

        AttachMapViewToActiveHost();
        UpdateMapPlaceholder(string.Empty, isVisible: false);
        PushMapState();
        return Task.CompletedTask;
    }

    private void AttachMapViewToActiveHost()
    {
        if (_mapService == null)
        {
            return;
        }

        var control = _mapService.MapControl;
        _mapHost.Children.Remove(control);
        _mapHost.Children.Add(control);
    }

    private void DetachViewModelEvents()
    {
        if (_mainViewModel == null)
        {
            return;
        }

        _mainViewModel.CameraPopoutRequested -= OnViewModelCameraPopoutRequested;
        _mainViewModel.CameraRenameRequested -= OnViewModelCameraRenameRequested;
        _mainViewModel.MapFlyToRequested -= OnViewModelMapFlyToRequested;
        _mainViewModel.LiveFeedSelectionChanged -= OnViewModelLiveFeedSelectionChanged;
        _mainViewModel.CaptureVisibleStreamsRequested -= OnViewModelCaptureVisibleStreamsRequested;
        _mainViewModel.RefreshVisibleFeedsRequested -= OnViewModelRefreshVisibleFeedsRequested;
        _mainViewModel.StopAllPlaybackRequested -= OnViewModelStopAllPlaybackRequested;
        _mainViewModel.StaticCameraGroupRequested -= OnViewModelStaticCameraGroupRequested;
        _mainViewModel.StaticCameraSelectionChanged -= OnViewModelStaticCameraSelectionChanged;
        _mainViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _mainViewModel.ShowStreamHealthChanged -= OnViewModelShowStreamHealthChanged;
    }

    private void OnViewModelShowStreamHealthChanged(object? sender, bool show)
    {
        _healthMonitor.ShowBadges = show;
    }

    private void OpenHealthPopup(CameraHealthState state)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OpenHealthPopup(state));
            return;
        }

        var popup = new StreamHealthPopup(state);
        popup.Show(this);
    }

    private async void OnViewModelCameraPopoutRequested(object? sender, CameraItem camera)
    {
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => OpenOrActivateCameraPopout(camera));
            _mapService?.SetSelection(camera.UniqueId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to open live camera view for '{camera.UniqueId}': {ex}");
            if (_mainViewModel != null)
            {
                _mainViewModel.RightPanelDiagnosticsStatus = $"Failed to open {camera.DisplayLabel}.";
            }
        }
    }

    private void OnViewModelMapFlyToRequested(object? sender, (double Lat, double Lng) e)
    {
        _mapService?.FlyTo(e.Lat, e.Lng);
    }

    private void OnViewModelLiveFeedSelectionChanged(object? sender, EventArgs e)
    {
        SyncEmbeddedFeedGrid();
        SyncCameraPreview();
        PushMapState();
    }

    private async void OnViewModelCaptureVisibleStreamsRequested(object? sender, EventArgs e)
    {
        StreamCaptureService.AppendDebugLog("Capture requested from shell command.");
        await CaptureVisibleStreamsAsync();
    }

    private void OnViewModelRefreshVisibleFeedsRequested(object? sender, EventArgs e)
    {
        RefreshVisibleFeeds();
    }

    private void OnViewModelStopAllPlaybackRequested(object? sender, EventArgs e)
    {
        _vlcService.StopAll();
        _mpvService.StopAll();
    }

    private async void OnViewModelStaticCameraGroupRequested(object? sender, StaticCameraGroupDefinition group)
    {
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => OpenOrActivateStaticCameraGroupPopout(group));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to open static camera group '{group.Id}': {ex}");
        }
    }

    private void SyncEmbeddedFeedGrid()
    {
        if (_mainViewModel == null) return;

        var selectedFeeds = _mainViewModel.FeedSelectorItems
            .Where(i => i.IsSelected)
            .OrderBy(i => i.SortOrder)
            .Take(MainViewModel.MaxSelectedFeeds)
            .Select(i => i.Feed)
            .ToList();

        if (selectedFeeds.Count == 0 && _rightPanelGrid == null)
        {
            return;
        }

        EnsureRightPanelGridInitialized();
        _rightPanelGrid!.SyncFeeds(selectedFeeds);


    }

    private void SyncCameraPreview()
    {
        if (_mainViewModel == null || _cameraPreviewHost == null) return;

        if (!_mainViewModel.IsCameraPreviewVisible)
        {
            _cameraPreviewGrid?.ClearAll();
            return;
        }

        var firstFeed = _mainViewModel.FeedSelectorItems
            .Where(i => i.IsSelected)
            .OrderBy(i => i.SortOrder)
            .Select(i => i.Feed)
            .FirstOrDefault();

        if (_cameraPreviewGrid == null)
        {
            _cameraPreviewGrid = new EmbeddedCameraGrid(_vlcService, _mpvService, _healthMonitor, OpenHealthPopup);
            _cameraPreviewHost.Child = _cameraPreviewGrid;
        }

        var feeds = firstFeed != null ? new List<LiveFeed> { firstFeed } : new List<LiveFeed>();
        _cameraPreviewGrid.SyncFeeds(feeds);
    }

    private void OnMainTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not TabControl tc) return;

        if (tc.SelectedIndex == 2)
        {
            EnsureStaticViewsGridInitialized();
            _embeddedStaticGrid?.Resume();
        }
        else
        {
            _embeddedStaticGrid?.Pause();
        }

        if (tc.SelectedIndex == 3)
            _mainViewModel?.LoadCaptures();

        if (tc.SelectedIndex == 4)
            EnsureSlideshowInitialized();
    }

    private void EnsureSlideshowInitialized()
    {
        if (_mainViewModel == null) return;

        if (!_slideshowSubscribed)
        {
            _mainViewModel.Slideshow.SlideshowChanged += OnSlideshowChanged;
            _slideshowSubscribed = true;
        }

        if (_slideshowGrid == null)
        {
            _slideshowGrid = new EmbeddedCameraGrid(_vlcService, _mpvService, _healthMonitor, OpenHealthPopup);
            _slideshowGrid.KeyDown += OnKeyDown;
            if (_slideshowGridHost != null)
                _slideshowGridHost.Child = _slideshowGrid;
        }

        if (!_mainViewModel.Slideshow.IsRunning)
        {
            _mainViewModel.Slideshow.Start();
        }
    }

    private void OnSlideshowChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnSlideshowChanged(sender, e));
            return;
        }

        if (_slideshowGrid == null || _mainViewModel == null) return;
        _slideshowGrid.SyncCameras(_mainViewModel.Slideshow.SlideshowCameras.ToList());
    }

    private void EnsureStaticViewsGridInitialized()
    {
        if (_embeddedStaticGrid != null) return;
        RebuildEmbeddedStaticGrid();
    }

    private void RebuildEmbeddedStaticGrid()
    {
        if (_mainViewModel == null || _staticViewsHost == null) return;

        if (_embeddedStaticGrid != null)
        {
            _embeddedStaticGrid.StatusChanged -= OnEmbeddedStaticGridStatusChanged;
            _embeddedStaticGrid.Teardown();
            _embeddedStaticGrid = null;
            _staticViewsHost.Child = null;
        }

        var groupsToShow = _mainViewModel.GetStaticCameraGroupsForDisplay();
        if (groupsToShow.Count == 0 || groupsToShow.All(g => g.Cameras.Count == 0)) return;

        _embeddedStaticGrid = new EmbeddedStaticCameraGrid(_staticSnapshotService, _healthMonitor, OpenHealthPopup);
        if (_staticTileSizeSlider != null)
            _embeddedStaticGrid.TileWidth = _staticTileSizeSlider.Value;
        _embeddedStaticGrid.StatusChanged += OnEmbeddedStaticGridStatusChanged;
        _staticViewsHost.Child = _embeddedStaticGrid;
        _embeddedStaticGrid.LoadAll(groupsToShow);
    }

    private void OnViewModelStaticCameraSelectionChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnViewModelStaticCameraSelectionChanged(sender, e));
            return;
        }
        RebuildEmbeddedStaticGrid();
    }

    private void OnMapStaticGroupCameraClicked(object? sender, string stableKey)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnMapStaticGroupCameraClicked(sender, stableKey));
            return;
        }

        var colonIdx = stableKey.IndexOf(':', StringComparison.Ordinal);
        var groupId = colonIdx > 0 ? stableKey[..colonIdx] : stableKey;
        _mainViewModel?.OpenStaticCameraGroupCommand.Execute(groupId);
    }

    private void OnEmbeddedStaticGridStatusChanged(object? sender, string status)
    {
        if (_staticViewsStatusText != null)
            _staticViewsStatusText.Text = status;
    }

    private void OnCaptureEntryPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(null).Properties.IsLeftButtonPressed &&
            sender is Border { DataContext: CaptureEntryViewModel entry })
        {
            _mainViewModel?.OpenCaptureCommand.Execute(entry);
        }
    }

    private void StaticViewsRefresh_Click(object? sender, RoutedEventArgs e)
    {
        EnsureStaticViewsGridInitialized();
        _embeddedStaticGrid?.RequestRefresh();
    }

    private void OnStaticTileSizeChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_embeddedStaticGrid != null)
            _embeddedStaticGrid.TileWidth = e.NewValue;
    }

    private void RefreshVisibleFeeds()
    {
        _rightPanelGrid?.RefreshAll();
        _cameraPreviewGrid?.RefreshAll();
        _slideshowGrid?.RefreshAll();

        if (_cameraGrid is { IsVisible: true } cameraGrid)
        {
            cameraGrid.RefreshAll();
        }

        foreach (var staticGrid in _staticCameraGrids.Values.Where(grid => grid.IsVisible))
        {
            staticGrid.RequestRefresh();
        }

        _embeddedStaticGrid?.RequestRefresh();
    }

    private void BuildStaticViewsMenu()
    {
        var staticViewsMenu = this.FindControl<MenuItem>("StaticViewsMenu");
        if (staticViewsMenu == null)
        {
            return;
        }

        staticViewsMenu.Items.Clear();

        if (_mainViewModel == null || _mainViewModel.StaticCameraGroups.Count == 0)
        {
            staticViewsMenu.IsEnabled = false;
            staticViewsMenu.Items.Add(new MenuItem
            {
                Header = "No static views available",
                IsEnabled = false
            });
            return;
        }

        staticViewsMenu.IsEnabled = true;
        foreach (var group in _mainViewModel.StaticCameraGroups)
        {
            staticViewsMenu.Items.Add(new MenuItem
            {
                Header = group.DisplayName,
                Command = _mainViewModel.OpenStaticCameraGroupCommand,
                CommandParameter = group.Id
            });
        }
    }

    private void BuildShellMenu()
    {
        if (_mainViewModel == null)
        {
            return;
        }

        var menu = new NativeMenu();
        menu.NeedsUpdate += (_, _) => UpdateShellMenuState();

        var viewMenu = new NativeMenu();
        _viewFullscreenMenuItem = new NativeMenuItem
        {
            Header = "Fullscreen",
            Command = _mainViewModel.ToggleFullscreenCommand,
            Gesture = new KeyGesture(Key.F11),
            ToggleType = NativeMenuItemToggleType.CheckBox
        };
        _viewCloseSnapshotMenuItem = new NativeMenuItem
        {
            Header = "Close Snapshot Overlay",
            Command = _mainViewModel.CloseSnapshotOverlayCommand
        };
        viewMenu.Add(_viewFullscreenMenuItem);
        viewMenu.Add(_viewCloseSnapshotMenuItem);

        var staticViewsMenu = new NativeMenu();
        if (_mainViewModel.StaticCameraGroups.Count > 0)
        {
            foreach (var group in _mainViewModel.StaticCameraGroups)
            {
                staticViewsMenu.Add(new NativeMenuItem
                {
                    Header = group.DisplayName,
                    Command = _mainViewModel.OpenStaticCameraGroupCommand,
                    CommandParameter = group.Id
                });
            }
        }
        else
        {
            staticViewsMenu.Add(new NativeMenuItem
            {
                Header = "No static views available",
                IsEnabled = false
            });
        }

        var captureMenu = new NativeMenu();
        captureMenu.Add(new NativeMenuItem
        {
            Header = "Capture Visible Streams",
            Command = _mainViewModel.CaptureVisibleStreamsCommand,
            Gesture = new KeyGesture(Key.S, KeyModifiers.Control | KeyModifiers.Shift)
        });

        var overlayMenu = new NativeMenu();
        _overlayAlprMenuItem = new NativeMenuItem
        {
            Header = "ALPR Readers",
            Command = _mainViewModel.ToggleAlprLayerCommand,
            ToggleType = NativeMenuItemToggleType.CheckBox
        };
        overlayMenu.Add(_overlayAlprMenuItem);

        var feedsMenu = new NativeMenu();
        feedsMenu.Add(new NativeMenuItem
        {
            Header = "Refresh Visible Feeds",
            Command = _mainViewModel.RefreshVisibleFeedsCommand
        });
        feedsMenu.Add(new NativeMenuItemSeparator());
        feedsMenu.Add(new NativeMenuItem
        {
            Header = "Clear Selected Feeds",
            Command = _mainViewModel.ClearSelectedFeedsCommand
        });

        var settingsMenu = new NativeMenu();
        _settingsLightThemeMenuItem = new NativeMenuItem
        {
            Header = "Light Theme",
            Command = _mainViewModel.SetThemeModeCommand,
            CommandParameter = AppThemeMode.Light,
            ToggleType = NativeMenuItemToggleType.Radio
        };
        _settingsDarkThemeMenuItem = new NativeMenuItem
        {
            Header = "Dark Theme",
            Command = _mainViewModel.SetThemeModeCommand,
            CommandParameter = AppThemeMode.Dark,
            ToggleType = NativeMenuItemToggleType.Radio
        };
        settingsMenu.Add(_settingsLightThemeMenuItem);
        settingsMenu.Add(_settingsDarkThemeMenuItem);

        menu.Add(new NativeMenuItem { Header = "View", Menu = viewMenu });
        menu.Add(new NativeMenuItem { Header = "Static Views", Menu = staticViewsMenu });
        menu.Add(new NativeMenuItem { Header = "Capture", Menu = captureMenu });
        menu.Add(new NativeMenuItem { Header = "Overlay", Menu = overlayMenu });
        menu.Add(new NativeMenuItem { Header = "Feeds", Menu = feedsMenu });
        menu.Add(new NativeMenuItem { Header = "Settings", Menu = settingsMenu });

        NativeMenu.SetMenu(this, menu);
    }

    private void UpdateShellMenuState()
    {
        if (_mainViewModel == null)
        {
            return;
        }

        if (_viewFullscreenMenuItem != null)
        {
            _viewFullscreenMenuItem.IsChecked = _mainViewModel.IsFullscreenMode;
        }

        if (_viewCloseSnapshotMenuItem != null)
        {
            _viewCloseSnapshotMenuItem.IsEnabled = _mainViewModel.IsSnapshotOverlayVisible;
        }

        if (_overlayAlprMenuItem != null)
        {
            _overlayAlprMenuItem.IsChecked = _mainViewModel.IsAlprEnabled;
        }

        if (_settingsLightThemeMenuItem != null)
            _settingsLightThemeMenuItem.IsChecked = _mainViewModel.SelectedThemeMode == AppThemeMode.Light;

        if (_settingsDarkThemeMenuItem != null)
            _settingsDarkThemeMenuItem.IsChecked = _mainViewModel.SelectedThemeMode == AppThemeMode.Dark;
    }

    private void RightPanelGrid_OnTileCloseRequested(object? sender, string feedId)
    {
        if (_mainViewModel == null) return;

        _mainViewModel.RemoveFeedFromSelectionCommand.Execute(feedId);
    }

    private async void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_mainViewModel == null)
        {
            return;
        }

        var propertyName = e.PropertyName;
        UpdateShellMenuState();

        if (propertyName == nameof(MainViewModel.IsDataLoaded))
        {
            BuildStaticViewsMenu();
            PushMapState();
            return;
        }

        if (propertyName == nameof(MainViewModel.IsFullscreenMode))
        {
            ApplyRequestedFullscreenMode(_mainViewModel.IsFullscreenMode);
            return;
        }

        if (propertyName == nameof(MainViewModel.IsCameraPreviewVisible))
        {
            SyncCameraPreview();
            return;
        }

        if (_mapService == null)
        {
            return;
        }

        if (propertyName == nameof(MainViewModel.SelectedCamera))
        {
            var selectedCameraId = _mainViewModel.SelectedCamera?.UniqueId;
            if (!string.IsNullOrWhiteSpace(selectedCameraId))
            {
                _mapService.SetSelection(selectedCameraId);
            }
            return;
        }

        if (propertyName == nameof(MainViewModel.IsLiveFeedMode))
        {
            PushMapState();
            return;
        }

        if (propertyName == nameof(MainViewModel.IsAlprEnabled))
        {
            _ = _mapService.ToggleAlprAsync(_mainViewModel.IsAlprEnabled);
            return;
        }

        if (propertyName == nameof(MainViewModel.SelectedThemeMode))
        {
            ApplyThemeMode(_mainViewModel.SelectedThemeMode);
            _mapService.SetTheme(_mainViewModel.SelectedThemeMode);
            return;
        }

        if (propertyName == nameof(MainViewModel.MapLayerMode))
        {
            _mapService.SetMapLayerMode(_mainViewModel.MapLayerMode);
            return;
        }

    }

    // ── Map controls ───────────────────────────────────────────────

    private void MainMapZoomIn_Click(object? sender, RoutedEventArgs e)
    {
        _mapService?.HandleAction(MapAction.ZoomIn);
    }

    private void MainMapZoomOut_Click(object? sender, RoutedEventArgs e)
    {
        _mapService?.HandleAction(MapAction.ZoomOut);
    }

    private void MainMapResetView_Click(object? sender, RoutedEventArgs e)
    {
        _mapService?.HandleAction(MapAction.ResetView);
    }


    private void OpenOrActivateCameraPopout(CameraItem camera)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OpenOrActivateCameraPopout(camera));
            return;
        }

        EnsureCameraGrid();
        _cameraGrid!.AddCamera(camera);

        if (!_cameraGrid.IsVisible)
            _cameraGrid.Show();
        else
            _cameraGrid.Activate();

        ReassertPopoutTopmost(_cameraGrid);
    }

    private void OpenOrActivateStaticCameraGroupPopout(StaticCameraGroupDefinition group)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OpenOrActivateStaticCameraGroupPopout(group));
            return;
        }

        var isNewWindow = !_staticCameraGrids.TryGetValue(group.Id, out var staticGrid);
        if (isNewWindow)
        {
            staticGrid = CreateStaticCameraGrid(group);
            _staticCameraGrids[group.Id] = staticGrid;
        }

        if (!staticGrid!.IsVisible)
        {
            staticGrid.Show();
        }
        else
        {
            staticGrid.Activate();
        }

        ReassertPopoutTopmost(staticGrid);
    }

    private void EnsureCameraGrid()
    {
        if (_cameraGrid != null)
            return;

        _cameraGrid = new CameraGridWindow(_vlcService, _mpvService, _healthMonitor, OpenHealthPopup);
        _cameraGrid.ApplyThemeMode(_mainViewModel?.SelectedThemeMode ?? ThemeService.CurrentMode);
        PositionCameraGridAtMainLowerLeft();
        AttachCameraGridEvents(_cameraGrid);
    }

    private void ApplyThemeMode(AppThemeMode mode)
    {
        ThemeService.Apply(mode);
        _cameraGrid?.ApplyThemeMode(mode);
        foreach (var grid in _staticCameraGrids.Values)
        {
            grid.ApplyThemeMode(mode);
        }
    }

    private void PositionCameraGridAtMainLowerLeft()
    {
        if (_cameraGrid == null)
            return;

        // Position the grid window at the lower-left of the main window.
        _cameraGrid.Position = ClampGridPosition(new PixelPoint(
            Position.X,
            Position.Y + GetMainWindowPixelHeight() - GetGridPixelHeight()));

        UpdateCameraGridOffset();
    }

    private void UpdateCameraGridOffset()
    {
        if (_cameraGrid == null)
            return;

        _cameraGridOffset = new PixelPoint(
            _cameraGrid.Position.X - Position.X,
            _cameraGrid.Position.Y - Position.Y);
    }

    private void AttachCameraGridEvents(CameraGridWindow grid)
    {
        grid.Opened += OnCameraGridStackingChanged;
        grid.Activated += OnCameraGridStackingChanged;
        grid.Deactivated += OnCameraGridStackingChanged;
        grid.PositionChanged += OnCameraGridPositionChanged;
        grid.KeyDown += OnKeyDown;
        grid.CaptureShortcutPressed += OnCameraGridCaptureShortcutPressed;
    }

    private void AttachStaticCameraGridEvents(StaticCameraGridWindow grid)
    {
        grid.Opened += OnCameraGridStackingChanged;
        grid.Activated += OnCameraGridStackingChanged;
        grid.Deactivated += OnCameraGridStackingChanged;
        grid.PositionChanged += OnStaticCameraGridPositionChanged;
        grid.Closed += OnStaticCameraGridClosed;
    }

    private void DetachCameraGridEvents(CameraGridWindow grid)
    {
        grid.Opened -= OnCameraGridStackingChanged;
        grid.Activated -= OnCameraGridStackingChanged;
        grid.Deactivated -= OnCameraGridStackingChanged;
        grid.PositionChanged -= OnCameraGridPositionChanged;
        grid.KeyDown -= OnKeyDown;
        grid.CaptureShortcutPressed -= OnCameraGridCaptureShortcutPressed;
    }

    private void DetachStaticCameraGridEvents(StaticCameraGridWindow grid)
    {
        grid.Opened -= OnCameraGridStackingChanged;
        grid.Activated -= OnCameraGridStackingChanged;
        grid.Deactivated -= OnCameraGridStackingChanged;
        grid.PositionChanged -= OnStaticCameraGridPositionChanged;
        grid.Closed -= OnStaticCameraGridClosed;
    }

    private void OnCameraGridStackingChanged(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            ReassertPopoutTopmost(window);
        }
    }

    private async void OnCameraGridCaptureShortcutPressed(object? sender, EventArgs e)
    {
        if (_mainViewModel?.CaptureVisibleStreamsCommand.CanExecute(null) == true)
        {
            _mainViewModel.CaptureVisibleStreamsCommand.Execute(null);
            return;
        }

        await CaptureVisibleStreamsAsync();
    }

    private void OnStaticCameraGridClosed(object? sender, EventArgs e)
    {
        if (sender is not StaticCameraGridWindow grid)
        {
            return;
        }

        DetachStaticCameraGridEvents(grid);
        if (!string.IsNullOrWhiteSpace(grid.CurrentGroupId))
        {
            _staticCameraGrids.Remove(grid.CurrentGroupId);
            _staticCameraGridOffsets.Remove(grid.CurrentGroupId);
            _staticGridsMovingFromMain.Remove(grid.CurrentGroupId);
        }
    }

    private StaticCameraGridWindow CreateStaticCameraGrid(StaticCameraGroupDefinition group)
    {
        var grid = new StaticCameraGridWindow(_staticSnapshotService);
        grid.LoadGroup(group, refreshNow: true);
        grid.ApplyThemeMode(_mainViewModel?.SelectedThemeMode ?? ThemeService.CurrentMode);
        AttachStaticCameraGridEvents(grid);
        PositionStaticCameraGridAtMainLowerRight(group.Id, grid, _staticCameraGrids.Count);
        return grid;
    }

    private void PositionStaticCameraGridAtMainLowerRight(
        string groupId,
        StaticCameraGridWindow grid,
        int cascadeIndex)
    {
        var cascadeOffset = 32 * Math.Max(0, cascadeIndex);
        var desired = new PixelPoint(
            Position.X + GetMainWindowPixelWidth() - GetPopoutPixelWidth(grid) - cascadeOffset,
            Position.Y + GetMainWindowPixelHeight() - GetPopoutPixelHeight(grid) - cascadeOffset);

        MoveStaticCameraGridFromMain(groupId, grid, ClampStaticGridPosition(grid, desired));
        UpdateStaticCameraGridOffset(groupId, grid);
    }

    private void UpdateStaticCameraGridOffset(string groupId, StaticCameraGridWindow grid)
    {
        _staticCameraGridOffsets[groupId] = new PixelPoint(
            grid.Position.X - Position.X,
            grid.Position.Y - Position.Y);
    }

    private void OnMapCameraClicked(object? sender, string cameraId)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnMapCameraClicked(sender, cameraId));
            return;
        }

        if (_mainViewModel?.TrySelectCameraById(cameraId) == true)
        {
            _mapService?.SetSelection(cameraId);
        }
    }

    private void OnMapFeedClicked(object? sender, string feedId)
    {
        if (_mainViewModel != null && _mainViewModel.FocusFeedById(feedId))
        {
            PushMapState();
        }
    }

    public void PushMapState()
    {
        if (_mapService == null || _mainViewModel == null)
        {
            return;
        }

        IEnumerable<CameraItem> trafficCameras = [];
        IEnumerable<CameraItem> hlsCameras = [];
        IEnumerable<LiveFeed> liveFeedMarkers = [];

        if (_mainViewModel.IsDataLoaded)
        {
            trafficCameras = _mainViewModel.AllMapCameras.Where(camera => camera.Source == CameraSource.LeftTraffic);
            hlsCameras = _mainViewModel.AllMapCameras.Where(camera => camera.Source == CameraSource.LeftHls);
            liveFeedMarkers = _mainViewModel.FeedSelectorItems
                .Select(item => item.Feed)
                .Where(feed => feed.Lat.HasValue && feed.Lng.HasValue);
        }

        var selectedFeedIds = _mainViewModel.FeedSelectorItems
            .Where(item => item.IsSelected)
            .Select(item => item.Feed.Id)
            .ToHashSet();

        _mapService.UpdateMarkers(
            trafficCameras.ToList(),
            hlsCameras.ToList(),
            liveFeedMarkers.ToList(),
            selectedFeedIds,
            _mainViewModel.SelectedCamera?.UniqueId);

        if (_mainViewModel.IsDataLoaded)
        {
            var staticGroupCamsWithCoords = _mainViewModel.StaticCameraGroups
                .SelectMany(g => g.Cameras)
                .Where(c => c.HasCoordinates
                       && !string.Equals(c.ParentGroupId, "houston-transtar",
                              StringComparison.OrdinalIgnoreCase))
                .ToList();
            _mapService.UpdateStaticGroupMarkers(staticGroupCamsWithCoords);
        }
    }

    private void LeftCameraList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_mainViewModel == null)
            return;

        if (sender is ListBox lb && lb.SelectedItem is CameraItem camera)
        {
            lb.SelectedItem = null;
            _mainViewModel.SelectCameraCommand.Execute(camera);
        }
    }

    private void CameraCard_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right) return;
        if (sender is not Control control || control.DataContext is not CameraItem camera) return;
        if (_mainViewModel == null) return;

        var menu = new ContextMenu
        {
            Items =
            {
                new MenuItem
                {
                    Header = "Rename",
                    Command = _mainViewModel.RenameCameraCommand,
                    CommandParameter = camera
                },
                new MenuItem
                {
                    Header = camera.IsFavorite ? "Unstar" : "Star",
                    Command = _mainViewModel.ToggleFavoriteCommand,
                    CommandParameter = camera
                }
            }
        };
        menu.Open(control);
        e.Handled = true;
    }

    private async void OnViewModelCameraRenameRequested(object? sender, CameraItem camera)
    {
        var dialog = new RenameDialog(camera.DisplayLabel)
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var accepted = await dialog.ShowDialog<bool>(this);
        if (accepted)
        {
            _mainViewModel?.ApplyCameraRename(camera, dialog.NewName);
        }
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            e.Handled = true;
            ToggleFullscreenFromInput();
            return;
        }

        if (e.Key == Key.Escape && _isFullscreenActive)
        {
            e.Handled = true;
            ExitFullscreenFromInput();
            return;
        }

        if (IsCaptureShortcut(e))
        {
            StreamCaptureService.AppendDebugLog($"Shortcut received from {sender?.GetType().Name ?? "unknown"}.");
            e.Handled = true;
            if (_mainViewModel?.CaptureVisibleStreamsCommand.CanExecute(null) == true)
            {
                _mainViewModel.CaptureVisibleStreamsCommand.Execute(null);
                return;
            }

            await CaptureVisibleStreamsAsync();
        }
    }

    private async Task CaptureVisibleStreamsAsync()
    {
        if (_mainViewModel == null)
        {
            StreamCaptureService.AppendDebugLog("Capture aborted: MainViewModel is null.");
            return;
        }

        var visibleLeftLiveCameras = _cameraGrid?.GetVisibleCameras() ?? Array.Empty<CameraItem>();
        var visibleStreams = await _mainViewModel.EnumerateVisibleStreamCapturesAsync(visibleLeftLiveCameras);
        StreamCaptureService.AppendDebugLog(
            $"Capture requested. LeftVisible={visibleLeftLiveCameras.Count}, Streams={visibleStreams.Count}.");

        if (visibleStreams.Count == 0)
        {
            _mainViewModel.RightPanelDiagnosticsStatus = "Captured 0 streams.";
            ShowCaptureToast(0, NotificationType.Information);
            return;
        }

        var outcome = await _captureService.CaptureAllAsync(visibleStreams);

        _mainViewModel.RightPanelDiagnosticsStatus = $"Captured {outcome.SuccessCount} streams.";
        ShowCaptureToast(
            outcome.SuccessCount,
            outcome.SuccessCount == outcome.TotalCount ? NotificationType.Success : NotificationType.Warning);
    }

    private static bool IsCaptureShortcut(KeyEventArgs e)
    {
        return e.Key == Key.S
            && e.KeyModifiers.HasFlag(KeyModifiers.Control)
            && e.KeyModifiers.HasFlag(KeyModifiers.Shift);
    }

    private void ShowCaptureToast(int capturedCount, NotificationType type)
    {
        _notificationManager.Show(
            $"Captured {capturedCount} streams",
            type,
            TimeSpan.FromSeconds(3),
            onClick: null,
            onClose: null,
            classes: Array.Empty<string>());
        StreamCaptureService.AppendDebugLog($"Toast requested: Captured {capturedCount} streams ({type}).");
    }

    private void ApplyRequestedFullscreenMode(bool shouldBeFullscreen)
    {
        if (_isFullscreenTransitionInProgress || shouldBeFullscreen == _isFullscreenActive)
        {
            return;
        }

        _isFullscreenTransitionInProgress = true;
        try
        {
            if (shouldBeFullscreen)
            {
                EnterFullscreenPresentation();
            }
            else
            {
                ExitFullscreenPresentation();
            }

            _isFullscreenActive = shouldBeFullscreen;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fullscreen transition failed: {ex.Message}");
            if (_mainViewModel != null && _mainViewModel.IsFullscreenMode != _isFullscreenActive)
            {
                _mainViewModel.IsFullscreenMode = _isFullscreenActive;
            }
        }
        finally
        {
            _isFullscreenTransitionInProgress = false;
            ReassertAllPopoutsTopmost();
        }
    }

    private void EnterFullscreenPresentation()
    {
        _restoreWindowState = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
        _restoreSystemDecorations = SystemDecorations;
        _restorePosition = Position;
        _restoreWidth = Bounds.Width;
        _restoreHeight = Bounds.Height;

        WindowState = WindowState.Normal;

        // Use a borderless manual fullscreen instead of WindowState.FullScreen.
        // The native fullscreen path clips the top of this layout on Linux.
        SystemDecorations = SystemDecorations.None;

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen == null)
        {
            return;
        }

        var scale = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
        Width = screen.Bounds.Width / scale;
        Height = screen.Bounds.Height / scale;
        Position = screen.Bounds.Position;
    }

    private void ExitFullscreenPresentation()
    {
        WindowState = WindowState.Normal;
        SystemDecorations = _restoreSystemDecorations;

        // Restore cached bounds explicitly to preserve pre-fullscreen geometry.
        if (_restoreWidth > 0)
        {
            Width = _restoreWidth;
        }

        if (_restoreHeight > 0)
        {
            Height = _restoreHeight;
        }

        Position = _restorePosition;
        WindowState = _restoreWindowState == WindowState.FullScreen ? WindowState.Normal : _restoreWindowState;
    }

    private void ToggleFullscreenFromInput()
    {
        if (_mainViewModel == null)
        {
            return;
        }

        _mainViewModel.IsFullscreenMode = !_mainViewModel.IsFullscreenMode;
    }

    private void ExitFullscreenFromInput()
    {
        if (_mainViewModel == null || !_mainViewModel.IsFullscreenMode)
        {
            return;
        }

        _mainViewModel.IsFullscreenMode = false;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_isShuttingDown)
        {
            return;
        }

        _isShuttingDown = true;

        DetachViewModelEvents();
        DataContextChanged -= OnDataContextChanged;
        Closing -= OnClosing;
        KeyDown -= OnKeyDown;
        PropertyChanged -= OnWindowPropertyChanged;
        Activated -= OnWindowActivated;
        PositionChanged -= OnWindowPositionChanged;
        if (_mapService != null)
        {
            _mapService.CameraMarkerClicked -= OnMapCameraClicked;
            _mapService.FeedMarkerClicked -= OnMapFeedClicked;
            _mapService.StaticGroupCameraMarkerClicked -= OnMapStaticGroupCameraClicked;
            _mapService.MapControl.KeyDown -= OnKeyDown;
        }
        if (_rightPanelGrid != null)
        {
            _rightPanelGrid.KeyDown -= OnKeyDown;
        }

        if (_cameraGrid != null)
        {
            DetachCameraGridEvents(_cameraGrid);
        }

        foreach (var staticGrid in _staticCameraGrids.Values.ToList())
        {
            DetachStaticCameraGridEvents(staticGrid);
        }

        foreach (var window in EnumerateSecondaryWindows())
        {
            try
            {
                window.Close();
            }
            catch
            {
                // Ignore cleanup errors.
            }
        }
        _cameraGrid = null;
        _staticCameraGrids.Clear();
        _staticCameraGridOffsets.Clear();
        _staticGridsMovingFromMain.Clear();
        if (_rightPanelGrid != null)
        {
            _rightPanelGrid.TileCloseRequested -= RightPanelGrid_OnTileCloseRequested;
            _rightPanelGrid.Teardown();
        }
        if (_embeddedStaticGrid != null)
        {
            _embeddedStaticGrid.StatusChanged -= OnEmbeddedStaticGridStatusChanged;
            _embeddedStaticGrid.Teardown();
            _embeddedStaticGrid = null;
        }
        if (_slideshowSubscribed && _mainViewModel != null)
        {
            _mainViewModel.Slideshow.SlideshowChanged -= OnSlideshowChanged;
            _slideshowSubscribed = false;
        }
        if (_slideshowGrid != null)
        {
            _slideshowGrid.KeyDown -= OnKeyDown;
            _slideshowGrid.Teardown();
            _slideshowGrid = null;
        }
        _mpvService.StopAll();

        _mapService?.Dispose();
        TryShutdownDesktopLifetime();
    }

    private IEnumerable<Window> EnumerateSecondaryWindows()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.Windows.Where(window => !ReferenceEquals(window, this)).ToList();
        }

        var windows = new List<Window>();
        if (_cameraGrid != null)
        {
            windows.Add(_cameraGrid);
        }

        windows.AddRange(_staticCameraGrids.Values);

        return windows;
    }

    private void TryShutdownDesktopLifetime()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        // Explicit desktop shutdown guarantees full process exit after MainWindow cleanup.
        desktop.Shutdown();
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Window.WindowStateProperty)
            return;

        if (!_isFullscreenTransitionInProgress && WindowState == WindowState.FullScreen)
        {
            _isFullscreenActive = true;
        }

        if (!_isFullscreenTransitionInProgress
            && _mainViewModel != null
            && _mainViewModel.IsFullscreenMode != _isFullscreenActive)
        {
            _mainViewModel.IsFullscreenMode = _isFullscreenActive;
        }

        var isMinimized = WindowState == WindowState.Minimized;
        if (_cameraGrid != null)
        {
            if (isMinimized)
            {
                _cameraGrid.Hide();
            }
            else if (!_cameraGrid.IsVisible && _cameraGrid.TileCount > 0)
            {
                _cameraGrid.Show();
                ReassertPopoutTopmost(_cameraGrid);
            }
        }

        foreach (var staticGrid in _staticCameraGrids.Values)
        {
            if (isMinimized)
            {
                staticGrid.Hide();
            }
            else if (!staticGrid.IsVisible && !string.IsNullOrWhiteSpace(staticGrid.CurrentGroupId))
            {
                staticGrid.Show();
                ReassertPopoutTopmost(staticGrid);
            }
        }

        if (!isMinimized)
        {
            ReassertAllPopoutsTopmost();
        }
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        ReassertAllPopoutsTopmost();
    }

    private void OnWindowPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (_cameraGrid is { IsVisible: true } grid)
        {
            MoveCameraGridFromMain(grid, ClampGridPosition(new PixelPoint(
                e.Point.X + _cameraGridOffset.X,
                e.Point.Y + _cameraGridOffset.Y)));
        }

        foreach (var entry in _staticCameraGrids)
        {
            var staticGrid = entry.Value;
            if (!staticGrid.IsVisible)
            {
                continue;
            }

            var offset = _staticCameraGridOffsets.TryGetValue(entry.Key, out var storedOffset)
                ? storedOffset
                : default;

            MoveStaticCameraGridFromMain(
                entry.Key,
                staticGrid,
                ClampStaticGridPosition(staticGrid, new PixelPoint(
                    e.Point.X + offset.X,
                    e.Point.Y + offset.Y)));
        }
    }

    private void OnCameraGridPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (_isMovingGridFromMain || _cameraGrid == null)
            return;

        // Clamp the grid within the main window bounds when the user drags it.
        var clamped = ClampGridPosition(e.Point);
        if (clamped != e.Point)
        {
            MoveCameraGridFromMain(_cameraGrid, clamped);
        }

        UpdateCameraGridOffset();
    }

    private void OnStaticCameraGridPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (sender is not StaticCameraGridWindow grid || string.IsNullOrWhiteSpace(grid.CurrentGroupId))
        {
            return;
        }

        var groupId = grid.CurrentGroupId;
        if (_staticGridsMovingFromMain.Contains(groupId))
        {
            return;
        }

        var clamped = ClampStaticGridPosition(grid, e.Point);
        if (clamped != e.Point)
        {
            MoveStaticCameraGridFromMain(groupId, grid, clamped);
        }

        UpdateStaticCameraGridOffset(groupId, grid);
    }

    private void MoveCameraGridFromMain(CameraGridWindow grid, PixelPoint position)
    {
        _isMovingGridFromMain = true;
        try
        {
            grid.Position = position;
        }
        finally
        {
            _isMovingGridFromMain = false;
        }
    }

    private void MoveStaticCameraGridFromMain(string groupId, StaticCameraGridWindow grid, PixelPoint position)
    {
        _staticGridsMovingFromMain.Add(groupId);
        try
        {
            grid.Position = position;
        }
        finally
        {
            _staticGridsMovingFromMain.Remove(groupId);
        }
    }

    private PixelPoint ClampGridPosition(PixelPoint desired)
    {
        return _cameraGrid == null
            ? desired
            : ClampPopoutPosition(_cameraGrid, desired);
    }

    private PixelPoint ClampStaticGridPosition(StaticCameraGridWindow grid, PixelPoint desired)
    {
        return ClampPopoutPosition(grid, desired);
    }

    private PixelPoint ClampPopoutPosition(Window window, PixelPoint desired)
    {
        int mainX = Position.X;
        int mainY = Position.Y;
        int mainW = GetMainWindowPixelWidth();
        int mainH = GetMainWindowPixelHeight();
        int gridW = GetPopoutPixelWidth(window);
        int gridH = GetPopoutPixelHeight(window);

        int x = Math.Max(mainX, Math.Min(desired.X, mainX + mainW - gridW));
        int y = Math.Max(mainY, Math.Min(desired.Y, mainY + mainH - gridH));

        return new PixelPoint(x, y);
    }

    private int GetMainWindowPixelWidth()
    {
        return DipToPixels(Bounds.Width);
    }

    private int GetMainWindowPixelHeight()
    {
        return DipToPixels(Bounds.Height);
    }

    private int GetGridPixelHeight()
    {
        if (_cameraGrid == null) return 0;
        return GetPopoutPixelHeight(_cameraGrid);
    }

    private int GetPopoutPixelWidth(Window window)
    {
        var width = window.Bounds.Width > 0 ? window.Bounds.Width : window.Width;
        return DipToPixels(width);
    }

    private int GetPopoutPixelHeight(Window window)
    {
        var height = window.Bounds.Height > 0 ? window.Bounds.Height : window.Height;
        return DipToPixels(height);
    }

    private int DipToPixels(double dip)
    {
        double scale = GetScreenScale();
        return (int)Math.Round(dip * scale);
    }

    private double GetScreenScale()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        return screen?.Scaling ?? 1.0;
    }

    private void ReassertAllPopoutsTopmost()
    {
        if (_cameraGrid != null)
            ReassertPopoutTopmost(_cameraGrid);

        foreach (var grid in _staticCameraGrids.Values)
            ReassertPopoutTopmost(grid);
    }

    private static void ReassertPopoutTopmost(Window? window)
    {
        if (window == null || !window.IsVisible)
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsLinux())
            {
                // Linux WMs are much more sensitive to rapid topmost flips, and the
                // popouts are already declared Topmost in XAML.
                if (!window.Topmost)
                {
                    window.Topmost = true;
                }

                return;
            }

            // Toggle off→on; some desktop WMs ignore a no-op Topmost=true assignment
            // and only re-stack the window when the value actually changes.
            window.Topmost = false;
            window.Topmost = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to reassert popout topmost: {ex.Message}");
        }
    }

    // ── Custom title bar chrome ───────────────────────────────────────────────

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void TitleBar_DoubleTapped(object? sender, TappedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void ChromeMinimize_Click(object? sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void ChromeMaximize_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void ChromeClose_Click(object? sender, RoutedEventArgs e)
        => Close();

    private void FileExit_Click(object? sender, RoutedEventArgs e)
        => Close();

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
