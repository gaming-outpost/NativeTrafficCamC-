# CLAUDE.md — WorldView / Coastal Command Center

## Project overview

WorldView (namespace `CoastalCommandCenter`) is a single-user Linux desktop app for monitoring traffic cameras and live public feeds, primarily Texas/Houston Transtar sources. Built with Avalonia 11 on .NET 8; runs on X11/XWayland (CachyOS/Arch). No test suite; working prototype.

## Stack and versions

| Package | Version |
|---|---|
| Avalonia (+Desktop, Fluent, Fonts.Inter) | 11.3.1 |
| Avalonia.Diagnostics (Debug only) | 11.3.1 |
| Mapsui.Avalonia + Mapsui.Tiling | 5.* |
| LibVLCSharp + LibVLCSharp.Avalonia | 3.9.3 |
| CommunityToolkit.Mvvm | 8.4.0 |
| Microsoft.Data.Sqlite | 8.0.8 |

Runtime binaries: `/usr/bin/mpv` (hard-coded), `yt-dlp` (PATH), `ffmpeg` (PATH).

Build: `./run.sh` (prepends `LD_LIBRARY_PATH=/usr/lib`). `./run-fast.sh` skips rebuild.

## Repo layout

```
/                              project root = .NET project root
├── CoastalCommandCenter.csproj / .sln
├── Program.cs                 entry point + global exception logging
├── App.axaml / App.axaml.cs   resources + composition root (no DI)
├── Data/                      JSON camera catalogs (seeded into SQLite on first run)
├── Themes/DefaultTheme.axaml  active theme (others exist but are unused)
├── captures/                  snapshot output
└── src/
    ├── Models/                CameraItem, LiveFeed, AppSettings, PlaybackTuning; Health/
    ├── Domain/Cameras/        persistence-shaped domain records (CameraRecord, …)
    ├── Application/
    │   ├── Abstractions/      ICameraCatalogService, ICameraRepository, IAppSettingsStore
    │   └── Services/          CameraCatalogService, CameraRecordMapper
    ├── Infrastructure/
    │   ├── Bootstrap/         SqliteCameraDataBootstrapper, LegacyJsonCameraSeedLoader
    │   ├── Persistence/       SqliteCameraRepository (raw ADO.NET, not EF)
    │   ├── Settings/          JsonAppSettingsStore
    │   └── LocalStoragePaths.cs
    ├── Services/
    │   ├── VlcPlayerService.cs      single LibVLC + pooled MediaPlayer
    │   ├── MpvPlayerService.cs      mpv subprocess + IPC per feed
    │   ├── YtDlpService.cs          YouTube → direct URL (10-min cache)
    │   ├── StaticSnapshotService.cs HTTP JPEG fetcher
    │   ├── StreamCaptureService.cs  mpv IPC screenshot + ffmpeg fallback
    │   ├── MapService.cs            Mapsui setup + marker layers
    │   ├── ThemeService.cs          brush mutation for light/dark
    │   ├── TileLoadGate.cs          process-wide semaphore (default 4)
    │   ├── Diagnostics/TileTiming.cs
    │   └── Health/                  StreamHealthMonitor + per-backend probes
    ├── ViewModels/
    │   ├── MainViewModel.cs         ~1 200 lines; central VM
    │   ├── CameraGroupViewModel.cs
    │   ├── Feeds/                   LiveFeedSelectorItem, LiveFeedCityGroupViewModel, …
    │   ├── Health/StreamHealthPopupViewModel.cs
    │   └── Converters/
    └── Views/
        ├── MainWindow.axaml(.cs)         shell (~1 500 lines code-behind)
        ├── CameraGridWindow.axaml(.cs)   floating grid + CameraGridTile (defined here)
        ├── EmbeddedCameraGrid.cs         in-tab variant of the tile grid
        ├── EmbeddedStaticCameraGrid.cs
        ├── MpvVideoHost.cs               NativeControlHost → X11 wid
        ├── CameraGridManager.cs          cell layout calculator (1–9 tiles)
        └── Controls/StreamHealthChart.cs
```

## Hard constraints

**No DI container.** Services are constructed by hand in `App.OnFrameworkInitializationCompleted` and passed via constructors. Do not introduce a DI container.

**No IPlayer abstraction.** VLC and mpv have asymmetric lifecycles (pooled `MediaPlayer` vs persistent subprocess + IPC). The explicit `if (_usesMpv)` branch in `CameraGridTile` is intentional. Do not add an `IPlayer` interface.

**Compiled bindings.** `AvaloniaUseCompiledBindingsByDefault=true`. Every `Window`/`UserControl` root needs `x:DataType`. Without it, bindings silently no-op at runtime.

**UI thread.** All Avalonia control mutation must happen on `Dispatcher.UIThread`. Use `InvokeAsync` or `Post` for any follow-up after background work. Service code never touches controls; it fires `EventHandler<…>` events.

**`async void` only for event handlers.** Everything else returns `Task`. All awaits inside services use `.ConfigureAwait(false)`. Long-running async methods accept and honor `CancellationToken`.

**Single LibVLC instance.** Creating multiple `LibVLC` in one process has caused crashes. `VlcPlayerService` owns one lazy instance and pools `MediaPlayer` objects. Never create a `LibVLC` outside that service.

**Tile-startup concurrency must be bounded.** Always acquire `TileLoadGate` before starting a network parse. Default limit is 4, clamped to `[1, 16]`.

**Dispose `Media` after `Play`, not before.** `Play(media)` retains its own reference; disposing before `Play` yields a black tile.

## Data flow

JSON (`Data/`) → seeded once into SQLite → `SqliteCameraRepository` → `CameraCatalogService` (returns `CameraRecord`) → `CameraRecordMapper` → POCO models (`CameraItem`, `LiveFeed`) → `MainViewModel` → `EmbeddedCameraGrid.SyncFeeds(…)` → `CameraGridTile` per feed.

SQLite is source of truth after first run. Editing JSON has no effect unless `~/.local/share/CoastalCommandCenter/cameras.sqlite` is deleted.

## Naming conventions

- Classes: `PascalCase`; private fields: `_camelCase`; file names match type names.
- ViewModels derive from `ObservableObject`; use `[ObservableProperty]` on private backing fields, `[RelayCommand]` on methods.
- Console logging only (`Console.WriteLine`), prefixed with tag: `[mpv]`, `[vlc]`, `[yt-dlp]`, `[capture]`, `[App]`, `[FATAL]`.
- URL scheme validation is explicit — see `CameraItem.IsAllowedUrl`, `VlcPlayerService.ValidateScheme`.

## On-disk state

| Path | Purpose |
|---|---|
| `~/.local/share/CoastalCommandCenter/cameras.sqlite` | camera catalog (delete to re-seed) |
| `~/.config/CoastalCommandCenter/settings.json` | app settings (`AppSettings`) |
| `${TMPDIR}/ccc-mpv-{guid}.sock` | mpv IPC socket per session |
| `${TMPDIR}/ccc-capture.log` | capture diagnostics |

## Three files most likely to need touching

1. **`src/Services/VlcPlayerService.cs` or `MpvPlayerService.cs`** — for any playback behavior, URL handling, pool sizing, or backend-specific fix.
2. **`Views/CameraGridWindow.axaml.cs`** (`CameraGridTile`) — for tile lifecycle, overlay logic, attach/detach events, or per-tile UI.
3. **`src/Models/AppSettings.cs` + `PlaybackTuning.cs`** — for any new tuning knob, persisted preference, or startup configuration that must survive restarts.

Secondary: `MainViewModel.cs` for feed selection logic; `App.axaml.cs` for wiring new services; `src/Infrastructure/Persistence/SqliteCameraRepository.cs` for catalog schema changes.
