using System.Net.Http;
using System.Text.Json;
using BruTile.Predefined;
using BruTile.Web;
using CoastalCommandCenter.Models;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.Tiling.Layers;
using Mapsui.UI.Avalonia;
using Color = Mapsui.Styles.Color;

namespace CoastalCommandCenter.Services;

public enum MapAction
{
    ZoomIn,
    ZoomOut,
    ResetView
}

public sealed class MapService : IDisposable
{
    private const double DefaultCenterLon = -97.5;
    private const double DefaultCenterLat = 31.0;
    private const int DefaultZoom = 6;
    private const int FlyToZoom = 14;
    private const long FlyToDurationMs = 800;

    // Texas bounding box
    private const double BoundsMinLon = -106.6;
    private const double BoundsMinLat = 25.8;
    private const double BoundsMaxLon = -93.5;
    private const double BoundsMaxLat = 36.5;

    public MapControl MapControl { get; }
    public Map Map { get; }

    public event EventHandler<string>? CameraMarkerClicked;
    public event EventHandler<string>? FeedMarkerClicked;
    public event EventHandler<string>? StaticGroupCameraMarkerClicked;

    private readonly WritableLayer _trafficCameraLayer = new() { Name = "TrafficCameras" };
    private readonly WritableLayer _hlsCameraLayer = new() { Name = "HlsCameras" };
    private readonly WritableLayer _liveFeedLayer = new() { Name = "LiveFeeds" };
    private readonly WritableLayer _staticGroupCameraLayer = new() { Name = "StaticGroupCameras" };
    private readonly WritableLayer _alprLayer = new() { Name = "ALPR" };

    private readonly Dictionary<string, TileLayer> _tileLayerCache = new();

    private string? _selectedCameraId;
    private readonly HashSet<string> _selectedFeedIds = new();
    private AppThemeMode _currentTheme = AppThemeMode.Light;
    private string _mapLayerMode = "Map";
    private bool _alprFetched;
    private bool _disposed;

    // Zoom resolutions for level-based zoom (Mapsui uses resolutions, not zoom levels)
    private static readonly double[] ZoomResolutions = Enumerable.Range(0, 20)
        .Select(z => 156543.03392804097 / Math.Pow(2, z))
        .ToArray();

    public MapService()
    {
        Map = new Map { CRS = "EPSG:3857" };
        MapControl = new MapControl { Map = Map };
    }

    public void Initialize()
    {
        // Set pan bounds to Texas
        var (minX, minY) = SphericalMercator.FromLonLat(BoundsMinLon, BoundsMinLat);
        var (maxX, maxY) = SphericalMercator.FromLonLat(BoundsMaxLon, BoundsMaxLat);
        Map.Navigator.OverridePanBounds = new MRect(minX, minY, maxX, maxY);
        Map.Navigator.OverrideZoomBounds = new MMinMax(ZoomResolutions[18], ZoomResolutions[4]);

        // Default tile layer
        ApplyActiveTileLayers();

        // Add marker layers
        Map.Layers.Add(_trafficCameraLayer);
        Map.Layers.Add(_hlsCameraLayer);
        Map.Layers.Add(_liveFeedLayer);
        Map.Layers.Add(_staticGroupCameraLayer);
        Map.Layers.Add(_alprLayer);
        _alprLayer.Enabled = false;

        // Set initial viewport
        var (cx, cy) = SphericalMercator.FromLonLat(DefaultCenterLon, DefaultCenterLat);
        Map.Navigator.CenterOnAndZoomTo(new MPoint(cx, cy), ZoomResolutions[DefaultZoom]);

        // Map background
        Map.BackColor = new Color(26, 26, 46); // #1a1a2e

        // Wire click events
        Map.Info += OnMapInfo;
    }

    public void UpdateMarkers(
        IReadOnlyList<CameraItem> trafficCameras,
        IReadOnlyList<CameraItem> hlsCameras,
        IReadOnlyList<LiveFeed> liveFeeds,
        HashSet<string> selectedFeedIds,
        string? selectedCameraId)
    {
        _selectedCameraId = selectedCameraId;
        _selectedFeedIds.Clear();
        foreach (var id in selectedFeedIds)
            _selectedFeedIds.Add(id);

        var palette = GetPalette(_currentTheme);

        RebuildLayer(_trafficCameraLayer, trafficCameras, camera =>
        {
            var isLive = camera.Kind == CameraKind.Hls;
            return CreateMarkerFeature(
                camera.Lng, camera.Lat,
                camera.UniqueId, "traffic",
                isLive ? palette.MarkerLive : palette.MarkerPrimary,
                palette.MarkerStroke,
                isLive ? 5 : 4,
                IsSelected(camera.UniqueId));
        });

        RebuildLayer(_hlsCameraLayer, hlsCameras, camera =>
            CreateMarkerFeature(
                camera.Lng, camera.Lat,
                camera.UniqueId, "hls-camera",
                palette.MarkerLive, palette.MarkerStroke,
                4, IsSelected(camera.UniqueId)));

        RebuildFeedLayer(liveFeeds, palette);

        Map.RefreshGraphics();
    }

    public void SetSelection(string? cameraId)
    {
        _selectedCameraId = cameraId;
        ApplySelectionStyles();
        Map.RefreshGraphics();
    }

    public void FlyTo(double lat, double lng, int zoom = FlyToZoom)
    {
        var (x, y) = SphericalMercator.FromLonLat(lng, lat);
        Map.Navigator.FlyTo(new MPoint(x, y), ZoomResolutions[Math.Clamp(zoom, 0, 19)], FlyToDurationMs);
    }

    public void ZoomIn() => Map.Navigator.ZoomIn(300);
    public void ZoomOut() => Map.Navigator.ZoomOut(300);

    public void ResetView()
    {
        var (cx, cy) = SphericalMercator.FromLonLat(DefaultCenterLon, DefaultCenterLat);
        Map.Navigator.CenterOnAndZoomTo(new MPoint(cx, cy), ZoomResolutions[DefaultZoom], 500);
    }

    public void SetTheme(AppThemeMode mode)
    {
        if (_currentTheme == mode) return;
        _currentTheme = mode;

        ApplyActiveTileLayers();
        Map.BackColor = mode switch
        {
            AppThemeMode.Light => new Color(236, 233, 216),   // #ece9d8 beige
            _ => new Color(10, 18, 32)                         // #0a1220 dark navy
        };

        // Rebuild marker styles with new palette
        RebuildAllMarkerStyles();
        Map.RefreshGraphics();
    }

    public void SetMapLayerMode(string mode)
    {
        _mapLayerMode = mode;
        ApplyActiveTileLayers();
        Map.RefreshGraphics();
    }

    public void UpdateStaticGroupMarkers(IReadOnlyList<StaticCameraDefinition> cameras)
    {
        var palette = GetPalette(_currentTheme);
        _staticGroupCameraLayer.Clear();

        foreach (var cam in cameras)
        {
            if (!cam.HasCoordinates) continue;
            var (x, y) = SphericalMercator.FromLonLat(cam.Longitude!.Value, cam.Latitude!.Value);
            var feature = new PointFeature(x, y);
            feature["id"]         = cam.StableKey;
            feature["kind"]       = "static-group";
            feature["baseRadius"] = 5.0;

            feature.Styles.Add(new SymbolStyle
            {
                SymbolType  = SymbolType.Ellipse,
                SymbolScale = 0.5,
                Fill        = new Brush(new Color(255, 140, 0)),
                Line        = new Pen(palette.MarkerStroke, 1),
                Opacity     = 0.9f
            });

            _staticGroupCameraLayer.Add(feature);
        }

        Map.RefreshGraphics();
    }

    public async Task ToggleAlprAsync(bool enabled)
    {
        _alprLayer.Enabled = enabled;
        if (enabled && !_alprFetched)
        {
            await FetchAlprReadersAsync();
        }
        Map.RefreshGraphics();
    }

    public void SetLayerVisibility(string name, bool visible)
    {
        var layer = name switch
        {
            "traffic"       => (ILayer)_trafficCameraLayer,
            "hls"           => _hlsCameraLayer,
            "feeds"         => _liveFeedLayer,
            "static-group"  => _staticGroupCameraLayer,
            "alpr"          => _alprLayer,
            _               => null
        };
        if (layer != null)
        {
            layer.Enabled = visible;
            Map.RefreshGraphics();
        }
    }

    public void HandleAction(MapAction action)
    {
        switch (action)
        {
            case MapAction.ZoomIn: ZoomIn(); break;
            case MapAction.ZoomOut: ZoomOut(); break;
            case MapAction.ResetView: ResetView(); break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Map.Info -= OnMapInfo;
        MapControl.Dispose();
        Map.Dispose();
    }

    // --- Private helpers ---

    private void OnMapInfo(object? sender, MapInfoEventArgs e)
    {
        var mapInfo = e.GetMapInfo?.Invoke(e.Map.Layers);
        if (mapInfo?.Feature is not { } feature) return;

        var id = feature["id"]?.ToString();
        var kind = feature["kind"]?.ToString();
        if (string.IsNullOrWhiteSpace(id)) return;

        if (kind == "feed")
            FeedMarkerClicked?.Invoke(this, id);
        else if (kind == "static-group")
            StaticGroupCameraMarkerClicked?.Invoke(this, id);
        else
            CameraMarkerClicked?.Invoke(this, id);
    }

    private bool IsSelected(string id) =>
        id == _selectedCameraId || _selectedFeedIds.Contains(id);

    private static PointFeature CreateMarkerFeature(
        double lon, double lat,
        string id, string kind,
        Color fillColor, Color strokeColor,
        double baseRadius, bool selected)
    {
        var (x, y) = SphericalMercator.FromLonLat(lon, lat);
        var feature = new PointFeature(x, y);
        feature["id"] = id;
        feature["kind"] = kind;
        feature["baseRadius"] = baseRadius;

        var scale = selected ? 1.4 : 1.0;
        feature.Styles.Add(new SymbolStyle
        {
            SymbolType = SymbolType.Triangle,
            SymbolScale = baseRadius / 10.0 * scale,
            Fill = new Brush(fillColor),
            Line = new Pen(strokeColor, selected ? 2 : 1),
            Opacity = selected ? 1.0f : 0.9f
        });

        return feature;
    }

    private void RebuildLayer<T>(WritableLayer layer, IReadOnlyList<T> items, Func<T, PointFeature> factory)
    {
        layer.Clear();
        foreach (var item in items)
            layer.Add(factory(item));
    }

    private void RebuildFeedLayer(IReadOnlyList<LiveFeed> feeds, ThemePalette palette)
    {
        _liveFeedLayer.Clear();
        foreach (var feed in feeds)
        {
            if (!feed.Lat.HasValue || !feed.Lng.HasValue) continue;
            _liveFeedLayer.Add(CreateMarkerFeature(
                feed.Lng.Value, feed.Lat.Value,
                feed.Id, "feed",
                palette.MarkerPrimary, palette.MarkerStroke,
                7, IsSelected(feed.Id)));
        }
    }

    private void ApplySelectionStyles()
    {
        foreach (var layer in new WritableLayer[] { _trafficCameraLayer, _hlsCameraLayer, _liveFeedLayer, _staticGroupCameraLayer })
        {
            foreach (var feature in layer.GetFeatures())
            {
                var id = feature["id"]?.ToString();
                if (id == null) continue;
                var selected = IsSelected(id);
                var baseRadius = feature["baseRadius"] is double br ? br : 6.0;

                if (feature.Styles.FirstOrDefault() is SymbolStyle style)
                {
                    style.SymbolScale = baseRadius / 10.0 * (selected ? 1.4 : 1.0);
                    style.Opacity = selected ? 1.0f : 0.9f;
                    if (style.Line != null)
                        style.Line.Width = selected ? 2 : 1;
                }
            }
        }
    }

    private void RebuildAllMarkerStyles()
    {
        var palette = GetPalette(_currentTheme);

        foreach (var feature in _trafficCameraLayer.GetFeatures())
        {
            if (feature.Styles.FirstOrDefault() is not SymbolStyle style) continue;
            var kind = feature["kind"]?.ToString();
            var isLive = kind == "traffic" && feature["baseRadius"] is double br && br >= 7;
            style.Fill = new Brush(isLive ? palette.MarkerLive : palette.MarkerPrimary);
            style.Line = new Pen(palette.MarkerStroke, style.Line?.Width ?? 2);
        }

        foreach (var feature in _hlsCameraLayer.GetFeatures())
        {
            if (feature.Styles.FirstOrDefault() is not SymbolStyle style) continue;
            style.Fill = new Brush(palette.MarkerLive);
            style.Line = new Pen(palette.MarkerStroke, style.Line?.Width ?? 1);
        }

        foreach (var feature in _liveFeedLayer.GetFeatures())
        {
            if (feature.Styles.FirstOrDefault() is not SymbolStyle style) continue;
            style.Fill = new Brush(palette.MarkerPrimary);
            style.Line = new Pen(palette.MarkerStroke, style.Line?.Width ?? 2);
        }

        foreach (var feature in _staticGroupCameraLayer.GetFeatures())
        {
            if (feature.Styles.FirstOrDefault() is not SymbolStyle style) continue;
            style.Line = new Pen(palette.MarkerStroke, style.Line?.Width ?? 1);
        }

        RebuildAlprStyles(palette);
    }

    private void RebuildAlprStyles(ThemePalette palette)
    {
        foreach (var feature in _alprLayer.GetFeatures())
        {
            if (feature.Styles.FirstOrDefault() is not SymbolStyle style) continue;
            style.Fill = new Brush(palette.AlprMarker);
        }
    }

    // --- Tile layers ---

    private void ApplyActiveTileLayers()
    {
        var existing = Map.Layers.OfType<TileLayer>().ToList();
        foreach (var layer in existing)
            Map.Layers.Remove(layer);

        if (_mapLayerMode is "Sat" or "Hybrid")
        {
            Map.Layers.Insert(0, GetOrCreateTileLayer("SatImagery", CreateSatelliteImageryLayer));
            if (_mapLayerMode == "Hybrid")
                Map.Layers.Insert(1, GetOrCreateTileLayer("SatLabels", CreateSatelliteLabelsLayer));
        }
        else
        {
            Map.Layers.Insert(0, GetOrCreateTileLayer("CartoDark", CreateCartoDarkLayer));
        }
    }

    private TileLayer GetOrCreateTileLayer(string key, Func<TileLayer> factory)
    {
        if (!_tileLayerCache.TryGetValue(key, out var layer))
        {
            layer = factory();
            _tileLayerCache[key] = layer;
        }
        return layer;
    }

    private static TileLayer CreateCartoDarkLayer()
    {
        return new TileLayer(
            new HttpTileSource(
                new GlobalSphericalMercator(0, 20, "CARTO"),
                "https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}.png",
                new[] { "a", "b", "c", "d" },
                name: "CARTO Dark"))
        { Name = "CartoDark" };
    }

    private static TileLayer CreateSatelliteImageryLayer()
    {
        return new TileLayer(
            new HttpTileSource(
                new GlobalSphericalMercator(0, 20, "EsriSat"),
                "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}",
                name: "Esri World Imagery"))
        { Name = "SatImagery" };
    }

    private static TileLayer CreateSatelliteLabelsLayer()
    {
        return new TileLayer(
            new HttpTileSource(
                new GlobalSphericalMercator(0, 20, "EsriRef"),
                "https://server.arcgisonline.com/ArcGIS/rest/services/Reference/World_Boundaries_and_Places/MapServer/tile/{z}/{y}/{x}",
                name: "Esri World References"))
        { Name = "SatLabels" };
    }

    // --- ALPR ---

    private async Task FetchAlprReadersAsync()
    {
        try
        {
            // Use Texas-wide bounds for ALPR query
            var query = $"[out:json][timeout:25];(node[\"surveillance:type\"=\"ALPR\"]({BoundsMinLat},{BoundsMinLon},{BoundsMaxLat},{BoundsMaxLon}););out body;";
            using var client = new HttpClient();
            var response = await client.PostAsync(
                "https://overpass-api.de/api/interpreter",
                new StringContent(query));
            var json = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            var elements = doc.RootElement.GetProperty("elements");
            var palette = GetPalette(_currentTheme);

            _alprLayer.Clear();
            foreach (var el in elements.EnumerateArray())
            {
                if (!el.TryGetProperty("lat", out var latProp) ||
                    !el.TryGetProperty("lon", out var lonProp))
                    continue;

                var lat = latProp.GetDouble();
                var lon = lonProp.GetDouble();
                var (x, y) = SphericalMercator.FromLonLat(lon, lat);

                var feature = new PointFeature(x, y);
                feature["kind"] = "alpr";

                var tags = el.TryGetProperty("tags", out var tagsProp) ? tagsProp : default;
                var direction = tags.ValueKind == JsonValueKind.Object && tags.TryGetProperty("direction", out var d) ? d.GetString() : "?";
                var manufacturer = tags.ValueKind == JsonValueKind.Object && tags.TryGetProperty("manufacturer", out var m) ? m.GetString() : "?";

                feature["tooltip"] = $"ALPR Reader\nDirection: {direction}\nMfg: {manufacturer}";

                feature.Styles.Add(new SymbolStyle
                {
                    SymbolType = SymbolType.Ellipse,
                    SymbolScale = 0.4,
                    Fill = new Brush(palette.AlprMarker),
                    Line = new Pen(new Color(0, 0, 0), 2),
                    Opacity = 0.9f
                });

                _alprLayer.Add(feature);
            }

            _alprFetched = true;
            Map.RefreshGraphics();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ALPR fetch failed: {ex.Message}");
        }
    }

    // --- Theme palettes ---

    private record ThemePalette(Color MarkerPrimary, Color MarkerLive, Color MarkerStroke, Color AlprMarker);

    private static ThemePalette GetPalette(AppThemeMode mode) => mode switch
    {
        AppThemeMode.Light => new ThemePalette(
            MarkerPrimary: new Color(10, 36, 106),      // #0a246a navy
            MarkerLive: new Color(0, 170, 0),            // #00aa00 green
            MarkerStroke: new Color(255, 255, 255),      // #FFFFFF
            AlprMarker: new Color(42, 82, 152)),         // #2a5298
        _ => new ThemePalette(
            MarkerPrimary: new Color(0, 229, 204),       // #00E5CC bright teal
            MarkerLive: new Color(0, 204, 68),           // #00cc44
            MarkerStroke: new Color(20, 20, 20),         // #141414
            AlprMarker: new Color(255, 10, 84))          // #ff0a54
    };
}
