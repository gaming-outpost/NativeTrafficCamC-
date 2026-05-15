using System.Collections.ObjectModel;
using System.Timers;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CoastalCommandCenter.Application.Abstractions;
using CoastalCommandCenter.Domain.Cameras;
using CoastalCommandCenter.Models;
using CoastalCommandCenter.Services;
using static CoastalCommandCenter.Application.Services.CameraRecordMapper;
using Timer = System.Timers.Timer;

namespace CoastalCommandCenter.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    public const int MaxSelectedFeeds = 6;

    private readonly ICameraCatalogService _cameraCatalogService;
    private readonly IAppSettingsStore _settingsStore;
    private readonly YtDlpService _ytdlpService;
    private readonly StaticSnapshotService _staticSnapshotService;
    private readonly ICameraDataBootstrapper? _cameraDataBootstrapper;
    private readonly ICameraRepository? _cameraRepository;
    private readonly bool _isUsingFallbackCatalog;
    private readonly object _startupLoadLock = new();

    private bool _isBulkUpdatingSelection;
    private int _selectionSequence;
    private bool _rightPanelDiagnosticsInitialized;
    private bool _isBulkUpdatingStaticSelection;
    private CancellationTokenSource? _staticSelectionDebounceCts;

    private List<TrafficCamera> _rawTrafficCams = [];
    private List<HlsCamera> _rawHlsCams = [];
    private List<LiveFeed> _rawLiveFeeds = [];
    private List<StaticCameraGroupDefinition> _rawStaticCameraGroups = [];
    private List<CameraItem> _cachedMapCameras = [];
    private Dictionary<string, CameraItem> _mapCameraLookup = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _settingsDebounceCts;

    private DispatcherTimer? _clockTimer;
    private Timer? _refreshTimer;
    private Timer? _snapshotRefreshTimer;
    private int _refreshCountdown = 180;
    private CameraItem? _activeSnapshotCamera;
    private Task? _loadDataTask;

    [ObservableProperty] private string _currentTime = "--:--:--";
    [ObservableProperty] private int _cameraCount;
    [ObservableProperty] private int _hlsCount;
    [ObservableProperty] private string _refreshTimerText = "180s";
    [ObservableProperty] private bool _isLiveFeedMode = true;
    [ObservableProperty] private bool _isFeedSelectorOpen;
    [ObservableProperty] private bool _isStaticCameraSelectorOpen;
    [ObservableProperty] private int _selectedStaticCameraCount;
    [ObservableProperty] private bool _isAlprEnabled;
    [ObservableProperty] private string _mapLayerMode = "Map";
    [ObservableProperty] private bool _showStreamHealth;
    [ObservableProperty] private AppThemeMode _selectedThemeMode = AppThemeMode.Light;
    [ObservableProperty] private string _panelTitle = "◢ LIVE TRAFFIC CAMS";
    [ObservableProperty] private bool _isFullscreenMode;
    [ObservableProperty] private bool _ytDlpAvailable;
    [ObservableProperty] private string? _feedSelectionWarning;
    [ObservableProperty] private int _selectedFeedCount;
    [ObservableProperty] private bool _isSnapshotOverlayVisible;
    [ObservableProperty] private string _snapshotOverlayTitle = string.Empty;
    [ObservableProperty] private string? _snapshotOverlayImageUrl;
    [ObservableProperty] private string _rightPanelDiagnosticsStatus = "Diagnostics ready.";
    [ObservableProperty] private bool _isCameraPreviewVisible = true;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isDataLoaded;
    [ObservableProperty] private string _startupStatus = "Preparing startup...";

    [ObservableProperty] private CameraItem? _selectedCamera;
    [ObservableProperty] private StaticCameraPreviewViewModel? _staticCameraPreview1923ViewModel;

    // ── System Monitor ────────────────────────────────────────────────
    public int OnlineCameraCount => CameraCount;
    public int WarnCameraCount   => 0;
    public int OfflineCameraCount => 0;
    /// <summary>Pixel width of the auto-refresh progress fill (based on ~166px usable track width).</summary>
    public double RefreshProgressWidth => (180 - _refreshCountdown) / 180.0 * 166.0;
    public ObservableCollection<string> EventLog { get; } = [];

    // ── Camera search (left panel) ────────────────────────────────────
    [ObservableProperty] private string _cameraSearch = string.Empty;

    // ── Properties pane ───────────────────────────────────────────────
    public ObservableCollection<CameraPropertyRow> SelectedCameraProperties { get; } = [];

    public ObservableCollection<CameraItem> LeftPanelCameras { get; } = [];
    /// <summary>Cameras grouped for display in the left panel. Rebuilt by <see cref="PopulateLeftPanel"/>.</summary>
    public ObservableCollection<CameraGroupViewModel> LeftPanelGroups { get; } = [];
    public ObservableCollection<LiveFeedSelectorItem> FeedSelectorItems { get; } = [];
    public ObservableCollection<LiveFeedCityGroupViewModel> LiveFeedCityGroups { get; } = [];
    public ObservableCollection<StaticCameraGroupDefinition> StaticCameraGroups { get; } = [];
    public ObservableCollection<StaticCameraSelectorItem> StaticCameraSelectorItems { get; } = [];
    public ObservableCollection<StaticCameraLocationGroupViewModel> StaticCameraSelectorGroups { get; } = [];
    public ObservableCollection<FeedStateTabViewModel> StateTabs { get; } = [];
    [ObservableProperty] private string _selectedState = "Texas";
    [ObservableProperty] private bool _hasMultipleStates;

    // ── Captures tab ──────────────────────────────────────────────────
    public ObservableCollection<CaptureEntryViewModel> Captures { get; } = [];
    [ObservableProperty] private int _captureCount;
    [ObservableProperty] private CaptureEntryViewModel? _selectedCapture;

    // ── Slideshow tab ─────────────────────────────────────────────────
    public SlideshowViewModel Slideshow { get; } = new();
    public ObservableCollection<string> RotatingViewCities { get; } = [];
    [ObservableProperty] private string _selectedRotatingCity = "All Texas";

    public bool IsLightThemeSelected
    {
        get => SelectedThemeMode == AppThemeMode.Light;
        set { if (value && SelectedThemeMode != AppThemeMode.Light) SelectedThemeMode = AppThemeMode.Light; }
    }

    public bool IsDarkThemeSelected
    {
        get => SelectedThemeMode == AppThemeMode.Dark;
        set { if (value && SelectedThemeMode != AppThemeMode.Dark) SelectedThemeMode = AppThemeMode.Dark; }
    }

    public MainViewModel(
        ICameraCatalogService cameraCatalogService,
        IAppSettingsStore settingsStore,
        YtDlpService ytdlpService,
        StaticSnapshotService staticSnapshotService,
        ICameraDataBootstrapper? cameraDataBootstrapper = null,
        ICameraRepository? cameraRepository = null,
        bool isUsingFallbackCatalog = false)
    {
        _cameraCatalogService = cameraCatalogService;
        _settingsStore = settingsStore;
        _ytdlpService = ytdlpService;
        _staticSnapshotService = staticSnapshotService;
        _cameraDataBootstrapper = cameraDataBootstrapper;
        _cameraRepository = cameraRepository;
        _isUsingFallbackCatalog = isUsingFallbackCatalog;

        InitTimers();
    }

    public Task LoadDataAsync()
    {
        lock (_startupLoadLock)
        {
            _loadDataTask ??= LoadDataCoreAsync();
            return _loadDataTask;
        }
    }

    private async Task LoadDataCoreAsync()
    {
        StartupStatus = "Loading camera catalog...";

        try
        {
            var loadedData = await Task.Run(LoadCatalogData).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ApplyLoadedCatalog(loadedData);
                IsDataLoaded = true;
                IsLoading = false;
                var fallbackSuffix = _isUsingFallbackCatalog ? "  [JSON fallback — SQLite unavailable]" : string.Empty;
                StartupStatus = $"Ready: {_cachedMapCameras.Count:N0} map assets loaded.{fallbackSuffix}";
                OnPropertyChanged(nameof(AllMapCameras));
            });

            _ = Task.Run(ProbeYtDlpAvailability);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MainViewModel] Startup data load failed: {ex.Message}");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsLoading = false;
                StartupStatus = "Startup failed. See console for details.";
            });
        }
    }

    private LoadedCatalogData LoadCatalogData()
    {
        _cameraDataBootstrapper?.Initialize();

        var allCameras = _cameraCatalogService.LoadAllCameras()
            .OrderBy(camera => camera.SortOrder)
            .ThenBy(camera => camera.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(camera => camera.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var trafficCameras = allCameras
            .Where(camera => camera.SourceKind == CameraSourceKind.TrafficCamera)
            .Select(MapToTrafficCamera)
            .OrderBy(camera => camera.Location)
            .ThenBy(camera => camera.Name)
            .ToList();

        var hlsCameras = allCameras
            .Where(camera => camera.SourceKind == CameraSourceKind.LiveTrafficCamera
                && camera.FeedType == CameraFeedType.Hls)
            .Select(MapToHlsCamera)
            .OrderBy(camera => camera.Location)
            .ThenBy(camera => camera.Name)
            .ToList();

        var liveFeeds = allCameras
            .Where(camera => camera.SourceKind == CameraSourceKind.PublicLiveFeed)
            .Select(MapToLiveFeed)
            .ToList();

        var staticCameraGroups = _cameraCatalogService.LoadStaticCameraGroups().ToList();

        return new LoadedCatalogData(trafficCameras, hlsCameras, liveFeeds, staticCameraGroups);
    }

    private void ApplyLoadedCatalog(LoadedCatalogData loadedData)
    {
        _rawTrafficCams = loadedData.TrafficCameras;
        _rawHlsCams = loadedData.HlsCameras;
        _rawLiveFeeds = loadedData.LiveFeeds;
        _rawStaticCameraGroups = loadedData.StaticCameraGroups;

        var transtarGroup = BuildTranstarStaticGroup(_rawTrafficCams);
        if (transtarGroup.Cameras.Count > 0)
            _rawStaticCameraGroups.Insert(0, transtarGroup);

        CameraCount = _rawHlsCams.Count;
        HlsCount = _rawHlsCams.Count;

        _cachedMapCameras = BuildAllMapCameras();
        _mapCameraLookup = _cachedMapCameras
            .GroupBy(camera => camera.UniqueId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        PopulateStaticCameraGroups();
        InitializeStaticCameraPreview();
        PopulateLeftPanel();
        InitializeFeedSelector();
        InitializeStaticCameraSelector();
        InitializeRotatingViewCities();
        Slideshow.SetCatalog(_rawHlsCams.Select(CameraItem.FromHls).ToList());
    }

    private void PopulateLeftPanel()
    {
        LeftPanelCameras.Clear();

        PanelTitle = "◢ LIVE TRAFFIC CAMS";
        foreach (var cam in _rawHlsCams)
        {
            LeftPanelCameras.Add(CameraItem.FromHls(cam));
        }

        RebuildLeftPanelGroups();
    }

    private void PopulateStaticCameraGroups()
    {
        StaticCameraGroups.Clear();
        foreach (var group in _rawStaticCameraGroups)
        {
            StaticCameraGroups.Add(group);
        }
    }

    private void InitializeStaticCameraPreview()
    {
        var camera1923 = _rawStaticCameraGroups
            .SelectMany(g => g.Cameras)
            .FirstOrDefault(c => c.Id == "1923" || c.Name.Contains("1923", StringComparison.OrdinalIgnoreCase));

        if (camera1923 != null)
        {
            StaticCameraPreview1923ViewModel = new StaticCameraPreviewViewModel(
                _staticSnapshotService,
                camera1923);
        }
    }

    private static StaticCameraGroupDefinition BuildTranstarStaticGroup(
        IReadOnlyList<TrafficCamera> trafficCameras)
    {
        const string groupId = "houston-transtar";
        var cameras = trafficCameras
            .Where(c => c.Type.Equals("transtar", StringComparison.OrdinalIgnoreCase) && c.CamId.HasValue)
            .Select(c => new StaticCameraDefinition
            {
                Id              = $"transtar-{c.CamId}",
                Name            = c.Name,
                Subgroup        = c.Location,
                Location        = c.Location,
                Latitude        = c.Lat,
                Longitude       = c.Lng,
                BaseSnapshotUrl = $"https://www.houstontranstar.org/snapshots/cctv/{c.CamId}.jpg",
                ParentGroupId   = groupId,
                ParentGroupDisplayName = "Houston TranStar"
            })
            .ToList();
        return new StaticCameraGroupDefinition
        {
            Id                     = groupId,
            DisplayName            = "Houston TranStar",
            RefreshIntervalSeconds = 180,
            Cameras                = cameras
        };
    }

    private void InitializeStaticCameraSelector()
    {
        foreach (var item in StaticCameraSelectorItems)
            item.SelectionChanged -= OnStaticCameraSelectionChanged;

        StaticCameraSelectorItems.Clear();
        StaticCameraSelectorGroups.Clear();

        var savedIds = _settingsStore.Load().SelectedStaticCameraIds
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _isBulkUpdatingStaticSelection = true;
        var sortOrder = 0;
        foreach (var group in _rawStaticCameraGroups)
        {
            foreach (var cam in group.Cameras)
            {
                var item = new StaticCameraSelectorItem
                {
                    Camera    = cam,
                    SortOrder = sortOrder++
                };
                item.SelectionChanged += OnStaticCameraSelectionChanged;

                if (savedIds.Contains(cam.StableKey))
                    item.SetSelectedSilently(true);

                StaticCameraSelectorItems.Add(item);
            }
        }
        _isBulkUpdatingStaticSelection = false;

        RebuildStaticCameraSelectorGroups();
        UpdateSelectedStaticCameraCount();
    }

    private void InitializeRotatingViewCities()
    {
        RotatingViewCities.Clear();
        RotatingViewCities.Add("All Texas");

        var cities = _rawHlsCams
            .Select(cam => cam.Location)
            .Where(loc => !string.IsNullOrWhiteSpace(loc))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(loc => loc, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var city in cities)
            RotatingViewCities.Add(city);

        SelectedRotatingCity = "All Texas";
        UpdateRotatingViewCatalog();
    }

    private void RebuildStaticCameraSelectorGroups()
    {
        StaticCameraSelectorGroups.Clear();

        var groups = StaticCameraSelectorItems
            .GroupBy(item => string.IsNullOrWhiteSpace(item.Camera.Location)
                ? "Other" : item.Camera.Location,
                StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var vm = new StaticCameraLocationGroupViewModel { Location = group.Key };
            vm.IsExpanded = group.Any(i => i.IsSelected);
            foreach (var item in group.OrderBy(i => i.SortOrder))
                vm.Cameras.Add(item);
            StaticCameraSelectorGroups.Add(vm);
        }
    }

    private void OnStaticCameraSelectionChanged(object? sender, EventArgs e)
    {
        if (_isBulkUpdatingStaticSelection) return;
        SyncStaticCameraSelection();
    }

    private void SyncStaticCameraSelection()
    {
        UpdateSelectedStaticCameraCount();
        PersistSelectedStaticCameraIds();
        StaticCameraSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateSelectedStaticCameraCount()
        => SelectedStaticCameraCount = StaticCameraSelectorItems.Count(i => i.IsSelected);

    private void PersistSelectedStaticCameraIds()
    {
        var ids = StaticCameraSelectorItems
            .Where(i => i.IsSelected)
            .Select(i => i.Camera.StableKey)
            .ToList();

        _staticSelectionDebounceCts?.Cancel();
        _staticSelectionDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _staticSelectionDebounceCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1000, cts.Token);
                var settings = _settingsStore.Load();
                settings.SelectedStaticCameraIds = ids;
                _settingsStore.Save(settings);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);
    }

    partial void OnIsStaticCameraSelectorOpenChanged(bool value)
    {
        if (value) RebuildStaticCameraSelectorGroups();
    }

    public IReadOnlyList<StaticCameraGroupDefinition> GetStaticCameraGroupsForDisplay()
    {
        var selectedKeys = StaticCameraSelectorItems
            .Where(i => i.IsSelected)
            .Select(i => i.Camera.StableKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (selectedKeys.Count == 0)
            return _rawStaticCameraGroups;

        return _rawStaticCameraGroups
            .Select(g => new StaticCameraGroupDefinition
            {
                Id                     = g.Id,
                DisplayName            = g.DisplayName,
                RefreshIntervalSeconds = g.RefreshIntervalSeconds,
                Cameras                = g.Cameras
                    .Where(c => selectedKeys.Contains(c.StableKey))
                    .ToList()
            })
            .Where(g => g.Cameras.Count > 0)
            .ToList();
    }

    partial void OnCameraSearchChanged(string value) => RebuildLeftPanelGroups();

    private const string FavoritesGroupKey = "⭐ FAVORITES";

    private void RebuildLeftPanelGroups()
    {
        // Preserve expanded/collapsed state across rebuilds.
        var expandedState = LeftPanelGroups.ToDictionary(
            g => g.GroupKey,
            g => g.IsExpanded,
            StringComparer.OrdinalIgnoreCase);

        LeftPanelGroups.Clear();

        // Favorites group always sits at the top, unaffected by search.
        var favorites = LeftPanelCameras.Where(c => c.IsFavorite).ToList();
        if (favorites.Count > 0)
        {
            var favVm = new CameraGroupViewModel(FavoritesGroupKey)
            {
                IsExpanded = !expandedState.TryGetValue(FavoritesGroupKey, out var favExpanded) || favExpanded
            };
            foreach (var cam in favorites)
            {
                favVm.Cameras.Add(cam);
            }
            LeftPanelGroups.Add(favVm);
        }

        IEnumerable<CameraItem> source = LeftPanelCameras;
        if (!string.IsNullOrWhiteSpace(CameraSearch))
        {
            var term = CameraSearch;
            source = source.Where(c =>
                c.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                c.GroupKey.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                c.Location.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        var groups = source
            .GroupBy(c => c.GroupKey, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var vm = new CameraGroupViewModel(group.Key)
            {
                IsExpanded = !expandedState.TryGetValue(group.Key, out var wasExpanded) || wasExpanded
            };
            foreach (var cam in group.OrderBy(c => c.Lat).ThenBy(c => c.Lng))
            {
                vm.Cameras.Add(cam);
            }
            LeftPanelGroups.Add(vm);
        }
    }

    private void InitializeFeedSelector()
    {
        FeedSelectorItems.Clear();
        _selectionSequence = 0;

        // Restore persisted map layer mode.
        var savedSettings = _settingsStore.Load();
        var savedLayerMode = savedSettings.MapLayerMode;
        if (savedLayerMode is "Sat" or "Hybrid")
            MapLayerMode = savedLayerMode;

        // Restore persisted stream-health toggle.
        ShowStreamHealth = savedSettings.ShowStreamHealth;

        // Always start with one preferred stream on app launch.
        var selectedIds = BuildStartupSelectedIds();

        if (selectedIds.Count == 0)
        {
            selectedIds = new HashSet<string>(
                _rawLiveFeeds.Take(1).Select(f => f.Id),
                StringComparer.OrdinalIgnoreCase);
        }

        _isBulkUpdatingSelection = true;
        for (var i = 0; i < _rawLiveFeeds.Count; i++)
        {
            var feed = _rawLiveFeeds[i];
            var item = new LiveFeedSelectorItem
            {
                Feed = feed,
                SortOrder = i
            };

            item.SelectionChanged += OnFeedSelectionChanged;
            if (selectedIds.Contains(feed.Id))
            {
                item.SetSelectedSilently(true, ++_selectionSequence);
            }
            else
            {
                item.SetSelectedSilently(false);
            }
            FeedSelectorItems.Add(item);
        }

        _isBulkUpdatingSelection = false;

        BuildStateTabs();
        BuildCityGroups();
        SyncSelectedTilesWithSelection(startPlayback: false);
    }

    private HashSet<string> BuildStartupSelectedIds()
    {
        var savedIds = _settingsStore.Load().SelectedLiveFeedIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Take(MaxSelectedFeeds)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        savedIds.IntersectWith(_rawLiveFeeds.Select(feed => feed.Id));
        if (savedIds.Count > 0)
        {
            return savedIds;
        }

        var preferredCauseway = _rawLiveFeeds.FirstOrDefault(feed =>
            feed.Name.Contains("Galveston Causeway", StringComparison.OrdinalIgnoreCase) ||
            feed.Id.Contains("causeway", StringComparison.OrdinalIgnoreCase));

        var fallback = preferredCauseway
            ?? _rawLiveFeeds.FirstOrDefault(feed => feed.IsHls && !string.IsNullOrWhiteSpace(feed.Url))
            ?? _rawLiveFeeds.FirstOrDefault(feed => !string.IsNullOrWhiteSpace(feed.Url));

        return fallback == null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>([fallback.Id], StringComparer.OrdinalIgnoreCase);
    }

    private void BuildStateTabs()
    {
        StateTabs.Clear();

        var states = FeedSelectorItems
            .Select(item => item.State)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => !string.Equals(s, "Texas", StringComparison.OrdinalIgnoreCase))
            .ThenBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (states.Count == 0)
        {
            states.Add("Texas");
        }

        if (!states.Any(s => string.Equals(s, SelectedState, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedState = states[0];
        }

        foreach (var state in states)
        {
            StateTabs.Add(new FeedStateTabViewModel
            {
                State = state,
                IsActive = string.Equals(state, SelectedState, StringComparison.OrdinalIgnoreCase)
            });
        }

        HasMultipleStates = StateTabs.Count > 1;
    }

    [RelayCommand]
    private void SelectStateTab(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return;
        }

        SelectedState = state;
        foreach (var tab in StateTabs)
        {
            tab.IsActive = string.Equals(tab.State, state, StringComparison.OrdinalIgnoreCase);
        }

        BuildCityGroups();
    }

    private void BuildCityGroups()
    {
        LiveFeedCityGroups.Clear();

        var filtered = FeedSelectorItems
            .Where(item => string.Equals(item.State, SelectedState, StringComparison.OrdinalIgnoreCase));

        var groups = filtered
            .GroupBy(item => item.City, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var cityGroup = new LiveFeedCityGroupViewModel
            {
                City = group.Key
            };

            foreach (var item in group.OrderBy(f => f.SortOrder))
            {
                cityGroup.Feeds.Add(item);
            }

            LiveFeedCityGroups.Add(cityGroup);
        }

        ApplyFeedSelectorGroupExpansion();
    }

    private void ApplyFeedSelectorGroupExpansion()
    {
        if (LiveFeedCityGroups.Count == 0)
        {
            return;
        }

        var expandedAny = false;
        foreach (var group in LiveFeedCityGroups)
        {
            var shouldExpand = group.Feeds.Any(item => item.IsSelected);
            group.IsExpanded = shouldExpand;
            expandedAny |= shouldExpand;
        }

        if (!expandedAny)
        {
            LiveFeedCityGroups[0].IsExpanded = true;
        }
    }


    [RelayCommand]
    private void SelectCamera(CameraItem? camera)
    {
        if (camera == null)
        {
            return;
        }

        SelectedCamera = camera;
        RebuildSelectedCameraProperties(camera);
        MapFlyToRequested?.Invoke(this, (camera.Lat, camera.Lng));

        // Live cameras (HLS/YouTube) always open in the camera grid window,
        // even if they originate from traffic-cameras.json.
        if (camera.IsLive)
        {
            HideSnapshotOverlay();
            CameraPopoutRequested?.Invoke(this, camera);
            return;
        }

        // Static traffic cameras show the snapshot overlay on the map.
        if (camera.Source == CameraSource.LeftTraffic)
        {
            StartSnapshotOverlay(camera);
            return;
        }

        HideSnapshotOverlay();
        CameraPopoutRequested?.Invoke(this, camera);
    }

    [RelayCommand]
    private void RenameCamera(CameraItem? camera)
    {
        if (camera == null) return;
        CameraRenameRequested?.Invoke(this, camera);
    }

    [RelayCommand]
    private void ToggleFavorite(CameraItem? camera)
    {
        if (camera == null) return;

        camera.IsFavorite = !camera.IsFavorite;
        _cameraRepository?.UpdateCameraFavorite(camera.DbId, camera.IsFavorite);
        RebuildLeftPanelGroups();
    }

    public void ApplyCameraRename(CameraItem camera, string? newName)
    {
        var trimmed = string.IsNullOrWhiteSpace(newName) ? null : newName.Trim();
        camera.CustomDisplayName = trimmed;
        _cameraRepository?.UpdateCameraDisplayName(camera.DbId, trimmed);
        RebuildLeftPanelGroups();
        if (SelectedCamera != null && ReferenceEquals(SelectedCamera, camera))
        {
            RebuildSelectedCameraProperties(camera);
        }
    }

    [RelayCommand]
    private void ToggleFeedSelector()
    {
        IsFeedSelectorOpen = !IsFeedSelectorOpen;
    }

    [RelayCommand]
    private void ToggleStaticCameraSelector()
        => IsStaticCameraSelectorOpen = !IsStaticCameraSelectorOpen;

    [RelayCommand]
    private void ClearStaticCameraSelection()
    {
        _isBulkUpdatingStaticSelection = true;
        foreach (var item in StaticCameraSelectorItems)
            item.SetSelectedSilently(false);
        _isBulkUpdatingStaticSelection = false;
        SyncStaticCameraSelection();
    }

    [RelayCommand]
    private void ToggleCameraPreview()
    {
        IsCameraPreviewVisible = !IsCameraPreviewVisible;
    }

    [RelayCommand]
    private void ClearSelectedFeeds()
    {
        _isBulkUpdatingSelection = true;
        foreach (var item in FeedSelectorItems)
        {
            item.SetSelectedSilently(false);
        }
        _isBulkUpdatingSelection = false;

        FeedSelectionWarning = null;
        SyncSelectedTilesWithSelection();
    }

    [RelayCommand]
    private void ToggleAlprLayer()
    {
        IsAlprEnabled = !IsAlprEnabled;
    }

    [RelayCommand]
    private void ToggleFullscreen()
    {
        IsFullscreenMode = !IsFullscreenMode;
    }

    [RelayCommand]
    private void CaptureVisibleStreams()
    {
        CaptureVisibleStreamsRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void RefreshVisibleFeeds()
    {
        RefreshVisibleFeedsRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void StopAllPlayback()
    {
        StopAllPlaybackRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void RefreshCaptures() => LoadCaptures();

    [RelayCommand]
    private void OpenCapture(CaptureEntryViewModel entry) => SelectedCapture = entry;

    [RelayCommand]
    private void CloseCapture() => SelectedCapture = null;

    [RelayCommand(CanExecute = nameof(CanNavigateCaptures))]
    private void NavigatePreviousCapture()
    {
        if (SelectedCapture == null || Captures.Count == 0) return;
        var idx = Captures.IndexOf(SelectedCapture);
        SelectedCapture = idx > 0 ? Captures[idx - 1] : Captures[^1];
    }

    [RelayCommand(CanExecute = nameof(CanNavigateCaptures))]
    private void NavigateNextCapture()
    {
        if (SelectedCapture == null || Captures.Count == 0) return;
        var idx = Captures.IndexOf(SelectedCapture);
        SelectedCapture = idx < Captures.Count - 1 ? Captures[idx + 1] : Captures[0];
    }

    private bool CanNavigateCaptures() => SelectedCapture != null && Captures.Count > 1;

    public void LoadCaptures()
    {
        Captures.Clear();

        var dir = StreamCaptureService.ResolveCaptureOutputDirectory();
        if (!Directory.Exists(dir))
        {
            CaptureCount = 0;
            return;
        }

        var entries = Directory.EnumerateFiles(dir, "*.png")
            .Select(f => new CaptureEntryViewModel(f))
            .OrderByDescending(e => e.Timestamp)
            .ToList();

        foreach (var entry in entries)
            Captures.Add(entry);

        CaptureCount = entries.Count;
        _ = LoadCaptureThumbnailsAsync(entries);
    }

    private static async Task LoadCaptureThumbnailsAsync(IEnumerable<CaptureEntryViewModel> entries)
    {
        foreach (var entry in entries)
            await entry.LoadThumbnailAsync();
    }

    [RelayCommand]
    private void NewSession()
    {
        // TODO: implement session create
    }

    [RelayCommand]
    private void OpenSession()
    {
        // TODO: implement session load
    }

    [RelayCommand]
    private void SaveSession()
    {
        // TODO: implement session save
    }

    [RelayCommand]
    private void OpenStaticCameraGroup(string? groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return;
        }

        var group = StaticCameraGroups.FirstOrDefault(item =>
            string.Equals(item.Id, groupId, StringComparison.OrdinalIgnoreCase));
        if (group == null)
        {
            return;
        }

        StaticCameraGroupRequested?.Invoke(this, group);
    }

    [RelayCommand]
    private void SetThemeMode(AppThemeMode mode)
    {
        SelectedThemeMode = mode;
    }

    [RelayCommand]
    private void PopOutSelectedCamera()
    {
        if (SelectedCamera != null)
            CameraPopoutRequested?.Invoke(this, SelectedCamera);
    }

    partial void OnCameraCountChanged(int value)
    {
        OnPropertyChanged(nameof(OnlineCameraCount));
    }

    partial void OnSelectedCameraChanged(CameraItem? value)
    {
    }

    private void RebuildSelectedCameraProperties(CameraItem? camera)
    {
        SelectedCameraProperties.Clear();
        if (camera == null) return;
        SelectedCameraProperties.Add(new CameraPropertyRow("ID",       camera.UniqueId));
        SelectedCameraProperties.Add(new CameraPropertyRow("Name",     camera.DisplayLabel));
        SelectedCameraProperties.Add(new CameraPropertyRow("Coordinates", $"{camera.Lat.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} {camera.Lng.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}"));
        SelectedCameraProperties.Add(new CameraPropertyRow("Location", camera.Location));
        SelectedCameraProperties.Add(new CameraPropertyRow("Status",   camera.IsLive ? "Live" : "Static"));
    }

    public string MapLayerModeLabel => MapLayerMode switch
    {
        "Sat" => "SAT",
        "Hybrid" => "HYB",
        _ => "MAP"
    };

    [RelayCommand]
    private void CycleMapLayer()
    {
        MapLayerMode = MapLayerMode switch
        {
            "Map" => "Sat",
            "Sat" => "Hybrid",
            _ => "Map"
        };
    }

    partial void OnMapLayerModeChanged(string value)
    {
        OnPropertyChanged(nameof(MapLayerModeLabel));
        PersistMapLayerMode(value);
    }

    [RelayCommand(CanExecute = nameof(CanExitFullscreen))]
    private void ExitFullscreen()
    {
        if (SelectedCapture != null)
        {
            SelectedCapture = null;
            return;
        }
        if (IsFullscreenMode)
            IsFullscreenMode = false;
    }

    private void OnFeedSelectionChanged(object? sender, EventArgs e)
    {
        if (_isBulkUpdatingSelection || sender is not LiveFeedSelectorItem changedItem)
        {
            return;
        }

        if (changedItem.IsSelected && FeedSelectorItems.Count(i => i.IsSelected) > MaxSelectedFeeds)
        {
            _isBulkUpdatingSelection = true;
            changedItem.SetSelectedSilently(false);
            _isBulkUpdatingSelection = false;

            FeedSelectionWarning = $"You can select up to {MaxSelectedFeeds} feeds.";
            return;
        }

        if (changedItem.IsSelected)
        {
            changedItem.SetSelectionSequence(++_selectionSequence);
        }
        else
        {
            changedItem.SetSelectionSequence(0);
        }

        FeedSelectionWarning = null;
        SyncSelectedTilesWithSelection();
    }

    public void InitializeRightPanelDiagnosticsOnViewLoad()
    {
        if (_rightPanelDiagnosticsInitialized)
        {
            return;
        }

        if (IsLoading)
        {
            RightPanelDiagnosticsStatus = "Loading live feeds...";
            return;
        }

        _rightPanelDiagnosticsInitialized = true;
        if (!FeedSelectorItems.Any(IsValidSelectableFeed))
        {
            RightPanelDiagnosticsStatus = "No valid live-feeds entries available.";
            Console.WriteLine("[RIGHT-PANEL] No playable feeds found in live-feeds.");
            return;
        }

        RightPanelDiagnosticsStatus = $"Inline grid ready for {SelectedFeedCount} selected feed(s).";
        Console.WriteLine($"[RIGHT-PANEL] Inline grid sync requested on view load for {SelectedFeedCount} selected feed(s).");
        SyncSelectedTilesWithSelection(startPlayback: false);
    }

    private void SyncSelectedTilesWithSelection(bool startPlayback = false)
    {
        var selectedItems = FeedSelectorItems
            .Where(i => i.IsSelected)
            .OrderBy(i => i.SortOrder)
            .Take(MaxSelectedFeeds)
            .ToList();

        if (selectedItems.Count > 0)
        {
            if (startPlayback)
            {
                RightPanelDiagnosticsStatus = $"Loading {selectedItems.Count} selected feed(s) in inline grid...";
            }
            else
            {
                RightPanelDiagnosticsStatus = $"Prepared {selectedItems.Count} selected feed(s) for inline grid.";
            }
        }
        else
        {
            RightPanelDiagnosticsStatus = "No live feed selected.";
        }

        SelectedFeedCount = selectedItems.Count;
        PersistSelectedFeedIds(selectedItems.Select(item => item.Feed.Id));
        LiveFeedSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsValidSelectableFeed(LiveFeedSelectorItem item)
    {
        return !string.IsNullOrWhiteSpace(item.Feed.Url);
    }

    private void PersistSelectedFeedIds(IEnumerable<string> selectedIds)
    {
        var ids = selectedIds.ToList();

        // Debounce: cancel any pending write and schedule a new one after 1s.
        // This prevents rapid-fire disk writes when the user clicks multiple checkboxes.
        _settingsDebounceCts?.Cancel();
        _settingsDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _settingsDebounceCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1000, cts.Token);
                var settings = _settingsStore.Load();
                settings.SelectedLiveFeedIds = ids;
                _settingsStore.Save(settings);
            }
            catch (OperationCanceledException)
            {
                // Debounce cancelled by a newer invocation — expected.
            }
        }, cts.Token);
    }

    private void PersistMapLayerMode(string mode)
    {
        _settingsDebounceCts?.Cancel();
        _settingsDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _settingsDebounceCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1000, cts.Token);
                var settings = _settingsStore.Load();
                settings.MapLayerMode = mode;
                _settingsStore.Save(settings);
            }
            catch (OperationCanceledException)
            {
                // Debounce cancelled — expected.
            }
        }, cts.Token);
    }

    partial void OnIsFeedSelectorOpenChanged(bool value)
    {
        if (value)
        {
            ApplyFeedSelectorGroupExpansion();
        }
    }

    partial void OnSelectedThemeModeChanged(AppThemeMode value)
    {
        OnPropertyChanged(nameof(IsLightThemeSelected));
        OnPropertyChanged(nameof(IsDarkThemeSelected));
    }

    public event EventHandler<bool>? ShowStreamHealthChanged;

    partial void OnShowStreamHealthChanged(bool value)
    {
        ShowStreamHealthChanged?.Invoke(this, value);
        PersistShowStreamHealth(value);
    }

    private void PersistShowStreamHealth(bool value)
    {
        _settingsDebounceCts?.Cancel();
        _settingsDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _settingsDebounceCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1000, cts.Token);
                var settings = _settingsStore.Load();
                settings.ShowStreamHealth = value;
                _settingsStore.Save(settings);
            }
            catch (OperationCanceledException)
            {
                // Debounce cancelled — expected.
            }
        }, cts.Token);
    }

    public bool TrySelectCameraById(string cameraId)
    {
        if (!TryGetMapCameraById(cameraId, out var camera))
        {
            return false;
        }

        SelectCamera(camera);
        return true;
    }

    public bool TryGetMapCameraById(string cameraId, out CameraItem camera)
    {
        if (string.IsNullOrWhiteSpace(cameraId))
        {
            camera = null!;
            return false;
        }

        return _mapCameraLookup.TryGetValue(cameraId, out camera!);
    }

    public bool FocusFeedById(string feedId)
    {
        if (string.IsNullOrWhiteSpace(feedId))
        {
            return false;
        }

        var selectorItem = FeedSelectorItems.FirstOrDefault(i => i.Feed.Id == feedId);
        if (selectorItem == null)
        {
            return false;
        }

        if (!selectorItem.IsSelected)
        {
            if (FeedSelectorItems.Count(i => i.IsSelected) >= MaxSelectedFeeds)
            {
                FeedSelectionWarning = $"You can select up to {MaxSelectedFeeds} feeds.";
                return false;
            }

            selectorItem.IsSelected = true;
        }

        IsFeedSelectorOpen = true;
        return true;
    }

    [RelayCommand]
    private void RemoveFeedFromSelection(string? feedId)
    {
        if (string.IsNullOrWhiteSpace(feedId))
        {
            return;
        }

        var selectorItem = FeedSelectorItems.FirstOrDefault(
            item => string.Equals(item.Feed.Id, feedId, StringComparison.OrdinalIgnoreCase));

        if (selectorItem is { IsSelected: true })
        {
            selectorItem.IsSelected = false;
        }
    }

    partial void OnIsFullscreenModeChanged(bool value)
    {
        ExitFullscreenCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedCaptureChanged(CaptureEntryViewModel? value)
    {
        ExitFullscreenCommand.NotifyCanExecuteChanged();
        NavigatePreviousCaptureCommand.NotifyCanExecuteChanged();
        NavigateNextCaptureCommand.NotifyCanExecuteChanged();
    }

    private bool CanExitFullscreen() => IsFullscreenMode || SelectedCapture != null;

    private void InitTimers()
    {
        // DispatcherTimer fires on the UI thread, avoiding cross-thread
        // PropertyChanged issues that System.Timers.Timer would cause.
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        var chicagoTz = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        _clockTimer.Tick += (_, _) =>
        {
            var chicago = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, chicagoTz);
            CurrentTime = chicago.ToString("M/d/yyyy  HH:mm:ss");

            _refreshCountdown--;
            if (_refreshCountdown <= 0)
            {
                _refreshCountdown = 180;
            }
            RefreshTimerText = $"{_refreshCountdown}s";
            OnPropertyChanged(nameof(RefreshProgressWidth));
        };
        _clockTimer.Start();

        // Cache TTL is 10 minutes — purging every 2 minutes is sufficient.
        _refreshTimer = new Timer(120_000);
        _refreshTimer.Elapsed += (_, _) => _ytdlpService.PurgeExpiredCache();
        _refreshTimer.Start();
    }

    [RelayCommand(CanExecute = nameof(CanCloseSnapshotOverlay))]
    private void CloseSnapshotOverlay()
    {
        HideSnapshotOverlay();
    }

    partial void OnIsSnapshotOverlayVisibleChanged(bool value)
    {
        CloseSnapshotOverlayCommand.NotifyCanExecuteChanged();
    }

    private void StartSnapshotOverlay(CameraItem camera)
    {
        _activeSnapshotCamera = camera;
        SnapshotOverlayTitle = camera.DisplayLabel;
        IsSnapshotOverlayVisible = true;

        RefreshSnapshotOverlay();

        _snapshotRefreshTimer?.Stop();
        _snapshotRefreshTimer?.Dispose();
        _snapshotRefreshTimer = new Timer(180_000); // 3 minutes to match TranStar snapshot cadence.
        _snapshotRefreshTimer.Elapsed += (_, _) => Dispatcher.UIThread.Post(RefreshSnapshotOverlay);
        _snapshotRefreshTimer.Start();
    }

    private void RefreshSnapshotOverlay()
    {
        if (_activeSnapshotCamera == null)
        {
            return;
        }

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        SnapshotOverlayImageUrl = _activeSnapshotCamera.BuildSnapshotUrl(timestamp);
    }

    private void HideSnapshotOverlay()
    {
        _snapshotRefreshTimer?.Stop();
        _snapshotRefreshTimer?.Dispose();
        _snapshotRefreshTimer = null;

        _activeSnapshotCamera = null;
        SnapshotOverlayImageUrl = null;
        SnapshotOverlayTitle = string.Empty;
        IsSnapshotOverlayVisible = false;
    }

    public async Task<IReadOnlyList<VisibleStreamCaptureRequest>> EnumerateVisibleStreamCapturesAsync(
        IReadOnlyList<CameraItem> visibleLeftLiveCameras)
    {
        var requests = new List<VisibleStreamCaptureRequest>();

        foreach (var selectedFeed in FeedSelectorItems
                     .Where(item => item.IsSelected)
                     .OrderBy(item => item.SortOrder)
                     .Select(item => item.Feed))
        {
            if (selectedFeed.IsYoutube)
            {
                var resolvedYoutubeUrl = await ResolveCaptureUrlAsync(selectedFeed);
                requests.Add(new VisibleStreamCaptureRequest(
                    selectedFeed.Id,
                    GetFeedRegion(selectedFeed),
                    selectedFeed.Name,
                    StreamCaptureBackend.Mpv,
                    resolvedYoutubeUrl,
                    selectedFeed.Id));
                continue;
            }

            requests.Add(new VisibleStreamCaptureRequest(
                selectedFeed.Id,
                GetFeedRegion(selectedFeed),
                selectedFeed.Name,
                StreamCaptureBackend.Vlc,
                selectedFeed.Url,
                null));
        }

        foreach (var camera in visibleLeftLiveCameras.Where(camera => camera.IsLive))
        {
            requests.Add(new VisibleStreamCaptureRequest(
                camera.UniqueId,
                GetCameraRegion(camera),
                camera.DisplayLabel,
                StreamCaptureBackend.Vlc,
                camera.StreamUrl,
                null));
        }

        return requests;
    }

    public event EventHandler<CameraItem>? CameraPopoutRequested;
    public event EventHandler<CameraItem>? CameraRenameRequested;
    public event EventHandler<(double Lat, double Lng)>? MapFlyToRequested;
    public event EventHandler? LiveFeedSelectionChanged;
    public event EventHandler? CaptureVisibleStreamsRequested;
    public event EventHandler? RefreshVisibleFeedsRequested;
    public event EventHandler? StopAllPlaybackRequested;
    public event EventHandler<StaticCameraGroupDefinition>? StaticCameraGroupRequested;
    public event EventHandler? StaticCameraSelectionChanged;

    public IReadOnlyList<CameraItem> AllMapCameras => _cachedMapCameras;

    private sealed record LoadedCatalogData(
        List<TrafficCamera> TrafficCameras,
        List<HlsCamera> HlsCameras,
        List<LiveFeed> LiveFeeds,
        List<StaticCameraGroupDefinition> StaticCameraGroups);

    private List<CameraItem> BuildAllMapCameras()
    {
        return _rawTrafficCams.Select(CameraItem.FromTraffic)
            .Concat(_rawHlsCams.Select(CameraItem.FromHls))
            .Concat(_rawLiveFeeds.Where(f => f.Lat.HasValue && f.Lng.HasValue).Select(CameraItem.FromLiveFeed))
            .ToList();
    }

    private void ProbeYtDlpAvailability()
    {
        var available = YtDlpService.IsAvailable();
        Dispatcher.UIThread.Post(() =>
        {
            YtDlpAvailable = available;
            if (!available)
            {
                Console.WriteLine("⚠ yt-dlp not found. YouTube feeds may fail until yt-dlp is installed.");
            }
        });
    }

    private void EnsureYtDlpAvailabilityChecked()
    {
        if (YtDlpAvailable)
        {
            return;
        }

        YtDlpAvailable = YtDlpService.IsAvailable();
        if (!YtDlpAvailable)
        {
            Console.WriteLine("⚠ yt-dlp not found. YouTube feeds may fail until yt-dlp is installed.");
        }
    }

    public void Dispose()
    {
        Slideshow.Dispose();
        _clockTimer?.Stop();
        _refreshTimer?.Dispose();
        _snapshotRefreshTimer?.Dispose();
        _settingsDebounceCts?.Cancel();
        _settingsDebounceCts?.Dispose();
        _staticSelectionDebounceCts?.Cancel();
        _staticSelectionDebounceCts?.Dispose();

        foreach (var selectorItem in FeedSelectorItems)
        {
            selectorItem.SelectionChanged -= OnFeedSelectionChanged;
        }

        foreach (var item in StaticCameraSelectorItems)
        {
            item.SelectionChanged -= OnStaticCameraSelectionChanged;
        }

        GC.SuppressFinalize(this);
    }

    private static string GetCameraRegion(CameraItem camera)
    {
        if (camera.Source == CameraSource.LeftHls && !string.IsNullOrWhiteSpace(camera.GroupKey))
        {
            return camera.GroupKey;
        }

        if (!string.IsNullOrWhiteSpace(camera.Location))
        {
            return camera.Location;
        }

        return camera.GroupKey;
    }

    private static string GetFeedRegion(LiveFeed feed)
    {
        if (!string.IsNullOrWhiteSpace(feed.City))
        {
            return feed.City;
        }

        return string.IsNullOrWhiteSpace(feed.Type) ? "LIVE" : feed.Type;
    }

    private async Task<string?> ResolveCaptureUrlAsync(LiveFeed feed)
    {
        if (feed.IsHls)
        {
            return feed.Url;
        }

        EnsureYtDlpAvailabilityChecked();
        var result = await _ytdlpService.ResolveStreamUrlAsync(feed.Url);
        return result.Success ? result.DirectUrl : null;
    }

    private bool CanCloseSnapshotOverlay() => IsSnapshotOverlayVisible;

    private void UpdateRotatingViewCatalog()
    {
        if (string.IsNullOrWhiteSpace(SelectedRotatingCity))
        {
            SelectedRotatingCity = "All Texas";
        }

        IEnumerable<CameraItem> filtered;
        if (SelectedRotatingCity == "All Texas")
        {
            filtered = _rawHlsCams.Select(CameraItem.FromHls);
        }
        else
        {
            filtered = _rawHlsCams
                .Where(cam => string.Equals(cam.Location, SelectedRotatingCity, StringComparison.OrdinalIgnoreCase))
                .Select(CameraItem.FromHls);
        }

        var filteredList = filtered.ToList();
        Slideshow.SetCatalog(filteredList);
        Slideshow.Shuffle();
    }

    partial void OnSelectedRotatingCityChanged(string value)
    {
        UpdateRotatingViewCatalog();
    }

}
