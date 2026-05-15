# CONTEXT.md — WorldView / Coastal Command Center

## How to use this document

This is a project context dump intended for a fresh chat session that has no
access to this codebase. Read it end-to-end before answering follow-ups. The
person asking questions is working in a local Avalonia .NET 8 desktop app on
Linux (CachyOS / Arch). They cannot grep, run code, or attach files for you,
so when concrete details matter — versions, file paths, signatures — assume
they are in this document and look here first. If something is not stated,
say "unknown" rather than guess.

## What this project is

WorldView (internal name "Coastal Command Center", repo namespace
`CoastalCommandCenter`) is a single-user Linux desktop application for
monitoring traffic cameras, beach cams, port cams, and other public live
feeds — primarily in Texas, with a Texas-shaped Mapsui basemap and a Houston
Transtar / TxDOT-heavy default catalog. It is an Avalonia 11 desktop app
running on .NET 8. It is not a web app, not a Blazor app, not WPF, and not
WinUI; advice that assumes any of those targets will not apply. It targets
Linux (developed on CachyOS / Arch with X11 / XWayland) but the source is
nominally cross-platform — there are runtime checks for Windows/macOS in
`LocalStoragePaths` — and in practice the code paths that matter (mpv `--wid`
embedding, `MpvVideoHost` returning an X11 window handle from
`NativeControlHost`) are X11-only. Wayland users have to set
`GDK_BACKEND=x11`.

The app is a working prototype / personal tool, somewhere between "weekend
project" and "small in-house ops tool." There is no test suite. The README
calls it `v4.2.1` but there is no formal release process; that is just the
string shown in the status bar. Camera definitions are checked into the repo
as JSON under `Data/`, and on first launch they are seeded into a local
SQLite database under `~/.local/share/CoastalCommandCenter/cameras.sqlite`.
After that, SQLite is the source of truth and JSON edits are not picked up
unless the database is deleted.

The single window is laid out as a three-panel shell with a custom
title bar (`SystemDecorations="None"`): a left **Camera Browser** tree, a
center **TabControl** with four tabs (Map View, Camera Grid, Static Views,
Captures), and a right **System Monitor** strip. The Camera Grid tab shows
up to 6 simultaneously selected live feeds in a flow-laid Canvas; YouTube
feeds are played by an embedded mpv subprocess and everything else by VLC.

## Stack and versions

There is exactly one project, `CoastalCommandCenter.csproj`, targeting
`net8.0` with `Nullable` and `ImplicitUsings` enabled. NuGet pins (verbatim
from the `.csproj`):

- Avalonia 11.3.1 (`Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`,
  `Avalonia.Fonts.Inter`)
- `Avalonia.Diagnostics` 11.3.1 (Debug-only)
- Mapsui.Avalonia 5.* and Mapsui.Tiling 5.* (native .NET map control,
  SkiaSharp rendering — no WebView, no Leaflet)
- LibVLCSharp 3.9.3 + LibVLCSharp.Avalonia 3.9.3 (uses the system `libvlc`)
- CommunityToolkit.Mvvm 8.4.0 (`[ObservableProperty]`, `RelayCommand`)
- Microsoft.Data.Sqlite 8.0.8

`AvaloniaUseCompiledBindingsByDefault` is `true`, so all `Window`/`UserControl`
roots set `x:DataType` and bindings are compile-time checked. There is no
DI container — services are constructed by hand in `App.OnFrameworkInitializationCompleted`
and threaded into the `MainWindow` and `MainViewModel` constructors.

External binaries the app shells out to at runtime:

- `/usr/bin/mpv` (hard-coded path in `MpvPlayerService.MpvBinaryPath`)
- `yt-dlp` (resolved via PATH; required only for YouTube live feeds)
- `ffmpeg` (resolved via PATH; used as a snapshot fallback by
  `StreamCaptureService` when mpv's IPC `screenshot-to-file` fails)

The README mentions CefGlue / Chromium / Leaflet for the map. **That is stale.**
The map is now Mapsui (native, SkiaSharp). No CEF, no embedded browser.

## Repository layout

The project root *is* the .NET project root — `CoastalCommandCenter.csproj`,
`Program.cs`, `App.axaml`, and `app.manifest` live at the top level alongside
the `src/`, `Data/`, `Themes/`, and `captures/` folders. There is no separate
`src/CoastalCommandCenter/` nesting.

```
CoastalCommandCenter/                  (working dir; not actually a git repo)
├── CoastalCommandCenter.csproj        single project, net8.0
├── CoastalCommandCenter.sln
├── Program.cs                         Avalonia entry point + global exception logging
├── App.axaml / App.axaml.cs           Resources + service composition root
├── app.manifest
├── run.sh / run-fast.sh               dotnet run wrappers (sets LD_LIBRARY_PATH)
├── README.md                          partly stale; trust this doc over the README
├── Data/
│   ├── traffic-cameras.json           Houston Transtar + TxDOT entries
│   ├── hls-cameras.json               left-panel HLS cameras
│   ├── live-feeds.json                right-panel HLS / YouTube feeds
│   └── static-camera-groups.json      grouped JPEG-snapshot cameras
├── Themes/
│   ├── DefaultTheme.axaml             active theme (the others are unused)
│   ├── GrayWinFormsTheme.axaml
│   ├── NordTheme.axaml
│   └── WindowsClassicTheme.axaml
├── captures/                          ad-hoc capture output committed to disk
└── src/
    ├── Models/                        POCOs: CameraItem, LiveFeed, AppSettings, PlaybackTuning,
    │   └── Health/                    health enums, CameraHealthState, HealthSample, etc.
    ├── Domain/Cameras/                Persistence-shaped domain records (CameraRecord, ...)
    ├── Application/
    │   ├── Abstractions/              ICameraCatalogService, ICameraRepository,
    │   │                              ICameraDataBootstrapper, IAppSettingsStore
    │   └── Services/                  CameraCatalogService, CameraRecordMapper
    ├── Infrastructure/
    │   ├── Bootstrap/                 SqliteCameraDataBootstrapper, LegacyJsonCameraSeedLoader,
    │   │                              LegacyJsonCameraCatalogService (fallback)
    │   ├── Persistence/               SqliteCameraRepository (raw ADO.NET, not EF)
    │   ├── Settings/                  JsonAppSettingsStore
    │   └── LocalStoragePaths.cs       XDG / AppData path resolution
    ├── Services/
    │   ├── VlcPlayerService.cs        LibVLC instance + MediaPlayer pool
    │   ├── MpvPlayerService.cs        mpv subprocess management + IPC
    │   ├── YtDlpService.cs            YouTube → direct stream URL
    │   ├── StaticSnapshotService.cs   HTTP JPEG fetcher
    │   ├── StreamCaptureService.cs    snapshot orchestration (mpv IPC + ffmpeg fallback)
    │   ├── MapService.cs              Mapsui setup, marker layers, tile providers
    │   ├── ThemeService.cs            light/dark palette mutation
    │   ├── TileLoadGate.cs            global semaphore around tile startup
    │   ├── Diagnostics/TileTiming.cs  ad-hoc structured timing logger
    │   └── Health/                    StreamHealthMonitor + per-backend probes
    ├── ViewModels/
    │   ├── MainViewModel.cs           1.2k lines; the central VM
    │   ├── CameraGroupViewModel.cs    left-panel collapsible groups
    │   ├── CaptureEntryViewModel.cs   thumbnails for the Captures tab
    │   ├── Feeds/                     LiveFeedSelectorItem, LiveFeedCityGroupViewModel,
    │   │                              FeedStateTabViewModel
    │   ├── Health/                    StreamHealthPopupViewModel
    │   └── Converters/                BoolToChevronConverter, NullToBoolConverter
    └── Views/
        ├── MainWindow.axaml(.cs)      shell window, ~1.5k lines of code-behind
        ├── CameraGridWindow.axaml(.cs) floating multi-tile window (legacy popout) +
        │                              CameraGridTile (the per-tile control)
        ├── EmbeddedCameraGrid.cs      same tile-flow logic, embedded into the Camera Grid tab
        ├── StaticCameraGridWindow.axaml(.cs) floating snapshot grid
        ├── EmbeddedStaticCameraGrid.cs in-tab variant for the Static Views tab
        ├── MpvVideoHost.cs            NativeControlHost that exposes its X11 wid for mpv
        ├── CameraGridManager.cs       static grid-cell layout calculator (1..9 tiles)
        ├── StreamHealthPopup.axaml(.cs)
        └── Controls/StreamHealthChart.cs
```

## Architecture and key patterns

**Composition root.** There is no DI container. `App.OnFrameworkInitializationCompleted`
constructs the services by hand and passes them into `MainWindow`'s constructor,
which in turn hands them to its `MainViewModel`. The relevant excerpt:

```csharp
var appSettingsStore = new JsonAppSettingsStore();
var settings = appSettingsStore.Load();
var tuning   = PlaybackTuning.FromSettings(settings);

var cameraRepository    = new SqliteCameraRepository();
var seedLoader          = new LegacyJsonCameraSeedLoader();
var cameraBootstrapper  = new SqliteCameraDataBootstrapper(cameraRepository, seedLoader);
var cameraCatalogService = new CameraCatalogService(cameraRepository);

var vlcService    = new VlcPlayerService();    vlcService.Configure(tuning);
var mpvService    = new MpvPlayerService();    mpvService.Configure(tuning);
var ytdlpService  = new YtDlpService();
var staticSnapshotService = new StaticSnapshotService();
var streamHealthMonitor   = new StreamHealthMonitor();

TileLoadGate.Configure(tuning.MaxParallelTileLoads);
vlcService.WarmPool(tuning.VlcWarmPoolSize);
Task.Run(mpvService.WarmBinary);

var vm = new MainViewModel(cameraCatalogService, appSettingsStore,
                           ytdlpService, cameraBootstrapper);
var mainWindow = new MainWindow(vlcService, mpvService,
                                staticSnapshotService, streamHealthMonitor) {
    DataContext = vm
};
desktop.MainWindow = mainWindow;
desktop.ShutdownRequested += (_, _) => {
    vm.Dispose(); vlcService.Dispose();
    staticSnapshotService.Dispose(); streamHealthMonitor.Dispose();
};
```

If `SqliteCameraRepository` construction throws, the app falls back to
`LegacyJsonCameraCatalogService`, which serves the same `ICameraCatalogService`
interface directly off the JSON files — useful when the SQLite file is
corrupt.

**Main shell.** `MainWindow.axaml` is a 6-row grid: custom title bar, menu
bar, toolbar, three-panel content (`210, 4, *, 4, 190` columns split by
`GridSplitter`s), a taskbar strip, and a status bar. Rows 0/1/2 are hidden
in fullscreen mode via the `fullscreen-hide` style class which is toggled by
`Classes.fullscreen-mode="{Binding IsFullscreenMode}"`. The center panel is
a `TabControl` with four tabs (Map View, Camera Grid, Static Views,
Captures); tabs are switched with mouse; `MainTabControl.SelectionChanged`
in code-behind lazily initializes the Static Views tab on first selection
because building snapshot tiles eagerly at startup was slow.

**Camera item model.** Every selectable thing on the left is uniformly a
`CameraItem` regardless of whether it came from a Houston Transtar JPEG, an
HLS m3u8, or a YouTube embed URL. The `Kind` enum (`Transtar | TxDot | Hls
| Youtube`) and `Source` enum (`LeftTraffic | LeftHls | RightFeed`) drive
later branching. `CameraItem.FromTraffic` / `FromHls` / `FromLiveFeed` build
these from the JSON-shaped DTOs.

**Player abstraction (or lack thereof).** There is no `IPlayer` interface
abstracting VLC vs mpv. The choice happens explicitly inside
`CameraGridTile.ctor` based on `_usesMpv = camera.Kind == CameraKind.Youtube`,
and the tile constructs either a `LibVLCSharp.Avalonia.VideoView` or a
custom `MpvVideoHost`. The two services (`VlcPlayerService`,
`MpvPlayerService`) have different shapes:

- `VlcPlayerService` owns a single `LibVLC` instance plus a pool of
  `MediaPlayer` objects. `Acquire()` pops one or creates one; `Release(player)`
  stops it and pushes it back. `PlayStreamAsync` constructs a `Media`,
  `await`s `Media.Parse(MediaParseOptions.ParseNetwork, timeout: 5000, ct)`,
  then calls `player.Play(media)` and disposes the `Media` (Play retains
  its own ref). Only `http`, `https`, `rtsp`, `rtp` URL schemes are accepted.

- `MpvPlayerService` keeps a `ConcurrentDictionary<string, ActiveSession>`
  keyed by feedId. Each session is `(Process, IpcPath, WindowId)`. `Start`
  spawns `/usr/bin/mpv` with `--wid={windowId}` to embed into the
  `MpvVideoHost`'s native X11 surface. Subsequent URL changes for the same
  feed go through the IPC unix socket as a JSON `loadfile … replace` command
  rather than restarting the process — this is the fast path for tile
  refresh.

`MpvVideoHost` itself is tiny (the whole class):

```csharp
public sealed class MpvVideoHost : NativeControlHost
{
    private IPlatformHandle? _handle;
    public nint NativeWindowId => _handle?.Handle ?? 0;
    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    { _handle = base.CreateNativeControlCore(parent); return _handle; }
    protected override void DestroyNativeControlCore(IPlatformHandle control)
    { _handle = null; base.DestroyNativeControlCore(control); }
}
```

**Tile lifecycle.** A `CameraGridTile : Panel` (defined at the bottom of
`Views/CameraGridWindow.axaml.cs`) owns one camera. Construction is cheap;
playback only starts when the tile is attached to the visual tree:

```csharp
EventHandler<VisualTreeAttachmentEventArgs>? onAttached = null;
onAttached = (_, _) => {
    AttachedToVisualTree -= onAttached;
    RestartPlayback();
};
AttachedToVisualTree += onAttached;
```

`RestartPlayback` cancels any in-flight `Parse`/`loadfile`, increments
`_playbackVersion`, and kicks `StartPlaybackAsync` on a worker. For mpv it
keeps the process alive across restarts (cheaper to swap the URL via IPC).
For VLC it `Release`s the player back into the pool. Tile startup is gated
by `TileLoadGate.AcquireAsync` so opening a 9-tile grid does not start nine
network parses simultaneously.

**Embedded grid vs floating window.** Both `EmbeddedCameraGrid` and
`CameraGridWindow` use the same per-tile control (`CameraGridTile`) and the
same layout calculator (`CameraGridManager.CalculateLayout(int count)`,
which returns rectangles in a fixed `560×420` logical-pixel grid that is
then scaled to fit the host). `EmbeddedCameraGrid` is the in-tab version
used by the Camera Grid tab; `CameraGridWindow` is a separate `Window` for
the legacy "pop out" flow.

**Camera flow, end to end.** SQLite (`SqliteCameraRepository`) →
`CameraCatalogService` (returns `CameraRecord` domain objects) →
`CameraRecordMapper.Map…` (maps to the legacy POCO models from
`Models/CameraModels.cs`) → `MainViewModel` populates
`LeftPanelGroups`, `LiveFeedCityGroups`, `StaticCameraGroups`,
`AllMapCameras` → `MapService.UpdateMarkers` adds Mapsui features to the
appropriate `WritableLayer` → user clicks a marker or checks a feed → the
view model raises an event → code-behind in `MainWindow` calls
`EmbeddedCameraGrid.SyncFeeds(...)`, which diffs the desired feed list and
adds/removes `CameraGridTile`s.

**Health monitoring.** `StreamHealthMonitor` runs one async sample loop per
registered camera. Each loop calls a backend-specific
`IStreamHealthProbe` (`MpvStreamHealthProbe`, `VlcStreamHealthProbe`,
`StaticSnapshotHealthProbe`) and writes results to a `CameraHealthState`.
The gear button on each tile opens `StreamHealthPopup` which charts the
recent samples. Badge visibility is controlled globally via
`StreamHealthMonitor.ShowBadges`, fed by the Settings menu's "Stream Health"
toggle.

**Settings.** `JsonAppSettingsStore` reads/writes a single
`settings.json` under `~/.config/CoastalCommandCenter/`. The model is
`AppSettings` and the playback-relevant fields are converted into a
`PlaybackTuning` immutable record at startup; `PlaybackTuning.FromSettings`
clamps every numeric field. Selected feed IDs, theme, "show stream health,"
and all VLC/mpv tuning knobs persist there.

## Conventions

The UI thread is sacred. All Avalonia control mutation goes through
`Dispatcher.UIThread`. Background work (parsing, HTTP, mpv IPC, snapshot
fetches) runs on `Task.Run` or directly on the threadpool, and any
follow-up control mutation is wrapped in
`Dispatcher.UIThread.InvokeAsync(...)` or `Dispatcher.UIThread.Post(...)`.
You will see this pattern repeatedly in `CameraGridTile.StartVlcAsync`,
`OpenHealthPopup`, etc. Service code never touches controls directly; it
exposes `EventHandler<...>` events that the view subscribes to.

`async void` is reserved for event handlers (`OnOpened`,
`OnViewModelCameraPopoutRequested`). Everything else returns `Task` and
calls `.ConfigureAwait(false)` on awaits inside services. `CancellationToken`
is threaded through every long-running async method; tiles cancel the
previous playback attempt when restarted by swapping
`Interlocked.Exchange(ref _playbackCts, new CancellationTokenSource())`.

ViewModels go in `src/ViewModels/`, derive from
`ObservableObject` (CommunityToolkit.Mvvm), use `[ObservableProperty]` on
private backing fields and `[RelayCommand]` on methods. The shell VM is
`MainViewModel`; sub-VMs (`CameraGroupViewModel`,
`LiveFeedSelectorItem`, etc.) are owned by it via `ObservableCollection<T>`.
Domain-shaped records live in `src/Domain/Cameras/`; persistence-shaped
mapping lives in `src/Application/Services/CameraRecordMapper.cs`.

Player code (anything that constructs a `MediaPlayer` or shells out to mpv)
goes in `src/Services/`. Per-tile glue goes in `CameraGridTile` inside
`CameraGridWindow.axaml.cs`. New playback features generally need touches
in three places: the service (`VlcPlayerService` / `MpvPlayerService`),
the tile (`CameraGridTile.Start{Vlc,Mpv}Async`), and the tuning model
(`PlaybackTuning` + `AppSettings`).

Naming: classes are PascalCase, private fields are `_camelCase`, file names
match type names. URL schemes are validated explicitly (see
`CameraItem.IsAllowedUrl`, `VlcPlayerService.ValidateScheme`,
`StaticSnapshotService.BuildCacheBustedUrl`). XAML uses
`x:DataType` everywhere because compiled bindings are on; if you forget the
DataType, the binding silently no-ops at runtime.

Do not introduce a DI container, an `IPlayer` abstraction, or a generic
"backend" enum-driven dispatch unless explicitly asked. The codebase
deliberately keeps the VLC and mpv paths separate because their lifecycle
shapes are not symmetric (pooled `MediaPlayer` vs reused subprocess + IPC).

## Build, run, debug

The dev box is CachyOS / Arch with these system packages pre-installed:
`dotnet-sdk` (for .NET 8), `vlc` (provides `libvlc.so` for LibVLCSharp),
`mpv` (the binary the app spawns), and `yt-dlp` (only needed for YouTube
feeds). Optional fonts: `ttf-ibm-plex` and `ttf-rajdhani`.

Build and run:

```bash
dotnet restore       # first time only, or after package changes
dotnet build         # debug build
./run.sh             # dotnet run, with LD_LIBRARY_PATH=/usr/lib prepended
./run-fast.sh        # dotnet run --no-build, for repeated launches
```

`run.sh` exists because LibVLCSharp's native loader can fail to find
`libvlc.so` if `LD_LIBRARY_PATH` is empty in some shell setups.
`CCC_ENABLE_LD_DEBUG_LIBS=1 ./run.sh` enables glibc's `LD_DEBUG=libs` for
diagnosing native-library load failures.

Release publish:

```bash
dotnet publish -c Release -r linux-x64 --self-contained
# Output: bin/Release/net8.0/linux-x64/publish/
```

Logs and on-disk state:

- App logs go to stdout/stderr (no file logger). `Console.WriteLine` is the
  logging mechanism throughout, prefixed by tag (`[mpv]`, `[yt-dlp]`,
  `[capture]`, `[App]`, `[FATAL]`).
- `TileTiming` (`src/Services/Diagnostics/TileTiming.cs`) emits structured
  timing spans for tile startup, gated on a debug flag.
- SQLite catalog: `~/.local/share/CoastalCommandCenter/cameras.sqlite`.
  Delete it to force a JSON re-seed.
- Settings: `~/.config/CoastalCommandCenter/settings.json`.
- Captures: written under the app's data directory; thumbnails are loaded
  back into the Captures tab.
- mpv IPC sockets: `${TMPDIR}/ccc-mpv-{guid}.sock`, deleted on process exit.
- Capture diagnostic log: `${TMPDIR}/ccc-capture.log`.

Debug aid: `Avalonia.Diagnostics` is referenced only in Debug, giving the
F12 dev-tools window.

## What's working, what's in progress, what's broken

Working: the four main tabs (Map / Camera Grid / Static Views / Captures);
both VLC and mpv backends; the feed selector; light/dark theme switching;
SQLite seeding from JSON; settings persistence; F11 fullscreen; per-tile
stream health probes and the gear-button popup; the Mapsui basemap with
zoom/pan/marker layers; ALPR overlay toggle in the Overlay menu.

In progress / partial: the multi-state UI is mostly Texas-only — the
`StateTabs` collection exists and `HasMultipleStates` is wired, but the
seed JSON only contains `state: "TX"` entries. The toolbar buttons
"New / Open / Save Session" are bound to commands but session
serialization is shallow. The Captures tab refreshes on demand only;
there is no live append while a capture is in flight.

Known fragile spots:

- The `MainWindow` `OnOpened` path triggers `LoadDataAsync` and
  `EnsureMapInitializedAsync` in parallel and then synchronizes with
  `Task.WhenAll`. If the catalog load fails midway, the map can stay in
  the "Loading map..." placeholder state.
- mpv's `--wid` embedding only works on X11 — Wayland users have to
  `GDK_BACKEND=x11 ./run.sh`. The code forces `WAYLAND_DISPLAY=""` and
  re-exports `DISPLAY` for the spawned mpv process, but that does not help
  if Avalonia itself is running on the Wayland backend (the X11 wid will
  not be a real X window).
- yt-dlp's compatibility format selector hard-codes
  `cookies-from-browser=brave`. Users without Brave installed will get a
  yt-dlp error.
- The README in this repo is partly stale (CefGlue / Leaflet references).
  Trust the code and this document.

There are no automated tests.

## Gotchas and hard-won knowledge

**One LibVLC instance, many MediaPlayers.** Creating a fresh `LibVLC` per
tile is expensive (it spins up an internal thread pool and parses the
plugin cache) and creating multiple instances in the same process has
caused crashes in the past. `VlcPlayerService` enforces a single lazily
constructed `LibVLC` and pools `MediaPlayer` instances for reuse. The pool
is warmed at startup (`vlcService.WarmPool(tuning.VlcWarmPoolSize)`) so
the first tiles do not pay the per-player ctor cost.

**`Media.Parse` then `Play(media)`, then `Dispose(media)`.** `Play` retains
its own reference to the `Media`, so disposing the local handle in a
`finally` block is correct — but only after `Play` has been called.
Disposing before `Play` produces a black tile with no error.

**Tile-startup concurrency must be bounded.** A 9-tile grid that opens
nine network parses simultaneously will starve every tile equally and the
whole grid loads in slow lockstep. `TileLoadGate` is a process-wide
semaphore (default 4) wrapped around tile startup specifically to prevent
this. Default is 4; clamped to `[1, 16]`.

**mpv on Wayland.** Subprocess mpv embedding requires `--wid`, which
requires X11. The mpv launch sets `WAYLAND_DISPLAY=""` and forces
`DISPLAY=":0"` (or the host's `DISPLAY`) on the child process so that mpv
itself is X11-only. But Avalonia must also be on X11 — its
`NativeControlHost` returns a Wayland subsurface handle on Wayland, which
mpv cannot embed into. Wayland users run `GDK_BACKEND=x11 ./run.sh`.

**mpv reuse via IPC.** Killing and respawning mpv per URL change makes the
tile flicker hard and adds startup latency. The service keeps the process
alive and sends `loadfile <url> replace` over the unix-domain IPC socket
(`--input-ipc-server=...`) for URL changes. The socket may not exist
immediately after spawn — `WaitForIpcSocketAsync` polls
`File.Exists(ipcPath)` for up to 2 seconds before sending commands.

**ytdl_hook is required, not disabled.** The mpv args deliberately do
*not* set `ytdl=no` because YouTube URL resolution depends on mpv's
built-in `ytdl_hook` plus `--ytdl-format`. There is a comment to that
effect in `MpvPlayerService.CreateStartInfo`. The yt-dlp service exists for
the VLC path, where we resolve YouTube → direct URL ourselves.

**HLS / yt-dlp token expiry.** `YtDlpService` caches resolved direct URLs
for 10 minutes (`CacheTtl`). YouTube live URLs expire faster than that in
some cases; restarting playback re-resolves.

**Subprocess mpv has no "first-frame" event.** For VLC we hook `Playing`
and use it to hide the "Loading…" overlay. For mpv we just hide the
overlay optimistically after `LoadUrl`; if the process dies the health
probe surfaces the failure later.

**Avalonia layout under Canvas.** Children of a `Canvas` are measured with
`Size.Infinity`. `CameraGridTile.MeasureOverride` clamps infinities to 0
so Avalonia's layout engine does not reject the result; without that you
get tiles that do not arrange.

**Pre-paying startup costs.** `App` starts `Task.Run(mpvService.WarmBinary)`
which runs `mpv --version` purely to page in the binary and its shared
libraries. The first real spawn skips disk-fetch latency. Same idea for
`vlcService.WarmPool`.

**Camera URL building is shape-aware.** `CameraItem.BuildSnapshotUrl` adds
a `?arg=<unix-millis>` cache-buster, but it strips an existing `arg=`
first to avoid duplicates. Static snapshots use a similar pattern in
`StaticSnapshotService.BuildCacheBustedUrl`.

**Theme switching is brush mutation.** `ThemeService` does not swap
`StyleInclude`s — it mutates the `Color` of shared `SolidColorBrush`
resources defined in `App.axaml`. All consumers reference brushes through
`{DynamicResource BrushXxx}` so the change propagates automatically.
`Themes/GrayWinFormsTheme.axaml`, `NordTheme.axaml`,
`WindowsClassicTheme.axaml` exist on disk but are not currently included
from `App.axaml`; only `DefaultTheme.axaml` is active.

**SQLite is the source of truth after first run.** Editing the JSON files
under `Data/` after first launch has no effect until the SQLite file is
deleted. There is no "re-seed" command in the UI.

## Open questions and active design tensions

- **Player abstraction.** Should there be an `IPlayer` interface to unify
  the VLC and mpv paths? Today the per-tile dispatch is an explicit `if
  (_usesMpv)` branch in `CameraGridTile`. Pro: deduplicates lifecycle
  code. Con: the lifecycles are genuinely different (pooled `MediaPlayer`
  vs persistent subprocess) and a leaky abstraction may be worse than the
  duplication. No decision yet.

- **Pool sizing.** `VlcWarmPoolSize` defaults to 4 (matching the default
  `MaxParallelTileLoads`). Should it scale with the maximum visible tile
  count (currently capped at 6 selected feeds) or stay fixed? The pool
  grows on demand anyway — the question is only about pre-warming cost.

- **mpv on Wayland.** The current advice is "set `GDK_BACKEND=x11`."
  Long-term, switching to libmpv via P/Invoke instead of subprocess
  embedding would avoid the X11 dependency, but at the cost of bringing
  mpv's render API into the Avalonia process and dealing with shared GL
  contexts.

- **Catalog editing.** Today edits round-trip through deleting the SQLite
  file and re-seeding from JSON. A proper "edit camera in UI" flow would
  need either an admin pane or a sync-back mechanism. No design yet.

- **Map provider.** Mapsui works but requires custom marker layers and
  manual click hit-testing (`Map.Info`). Worth revisiting only if marker
  performance becomes a problem at >5k cameras.

- **Capture backend.** mpv IPC `screenshot-to-file` is the primary path;
  ffmpeg is a fallback. For HLS-only feeds (no mpv process running) we
  always go through ffmpeg, which is slower. Whether to spawn mpv just
  for capture, or to keep the ffmpeg path, is undecided.

## Glossary

- **Tile** — one camera's UI surface in the grid. The control class is
  `CameraGridTile`. A tile contains either a `VideoView` (VLC) or an
  `MpvVideoHost` (mpv) plus a header bar with title, gear, and close.

- **Grid** — the Canvas-laid arrangement of tiles. Two implementations:
  `EmbeddedCameraGrid` (in the Camera Grid tab) and `CameraGridWindow`
  (legacy floating window). Both delegate cell layout to
  `CameraGridManager.CalculateLayout(count)` which has hand-tuned shapes
  for 1, 2, 3, 4 tiles and a generic `cols × rows` for 5–9.

- **Backend** — VLC or mpv. Selected per tile by camera kind: YouTube uses
  mpv; everything else uses VLC. The `TileBackend` enum in
  `PlaybackTuning` is configuration-only (`Auto`, `VlcOnly`,
  `MpvForYoutube`); today only `Auto` / `MpvForYoutube` are observed.

- **Capture** — a single still-image snapshot of a live stream. Two
  paths: mpv's IPC `screenshot-to-file` (preferred) and an ffmpeg
  one-shot (fallback). Stored under the app data directory and surfaced
  in the Captures tab.

- **Feed** — a `LiveFeed` (right panel JSON) — typically YouTube live or a
  large public HLS stream. Distinguished from a "camera" which is a
  cataloged Transtar/HLS/TxDOT entry on the left panel.

- **Static camera** — a JPEG-snapshot camera with a polling refresh
  interval. Lives in `static-camera-groups.json`, displayed in the Static
  Views tab. Backed by `StaticSnapshotService` (HTTP GET) and
  `EmbeddedStaticCameraGrid`.

- **Pop out** — older flow that opens a `CameraGridWindow` as a separate
  floating OS window. Most current UI paths use the embedded grid in the
  Camera Grid tab instead.

- **Health probe** — backend-specific class that samples a stream's
  liveness. `MpvStreamHealthProbe` polls mpv IPC; `VlcStreamHealthProbe`
  hooks `MediaPlayer` events; `StaticSnapshotHealthProbe` watches the
  HTTP fetcher.

- **State tab** — the row of buttons inside the feed selector that filters
  feeds by US state. Currently always "Texas."

- **Transtar / TxDOT** — the two Texas DOT camera networks. Transtar
  serves JPEG snapshots at `traffic.houstontranstar.org`; TxDOT serves
  embedded viewers at `txdot.gov` URLs.

- **WorldView** — the user-facing product name shown in the title bar and
  status bar. The repo / namespace / .csproj uses `CoastalCommandCenter`,
  the project's earlier name. Treat the two as synonyms.
