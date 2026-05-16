# WorldView

A standalone Avalonia desktop application for monitoring Texas traffic cameras, beach cams, border crossing feeds, and live streams. Built with LibVLCSharp + MPV for native video playback and Mapsui for native map rendering.

## Architecture

```
CoastalCommandCenter/
├── CoastalCommandCenter.sln
├── CoastalCommandCenter.csproj
├── Program.cs                              # Entry point
├── App.axaml / App.axaml.cs               # Dark tactical theme + service initialization
├── src/
│   ├── Models/
│   │   ├── CameraModels.cs                # Camera, LiveFeed, RadioFeed POCOs
│   │   ├── StaticCameraModels.cs          # Static snapshot camera models
│   │   ├── StreamCaptureModels.cs         # Capture/recording models
│   │   ├── AppSettings.cs                 # Application settings model
│   │   └── AppThemeMode.cs                # Theme enum
│   ├── Domain/Cameras/                    # Domain models for camera catalog
│   ├── ViewModels/
│   │   ├── MainViewModel.cs               # Core MVVM logic (cycling, selection)
│   │   ├── CommandCenterViewModel.cs      # Command center panel logic
│   │   ├── CameraGroupViewModel.cs        # Camera group display logic
│   │   ├── Feeds/                         # Feed-specific view models
│   │   └── Converters/                    # Value converters
│   ├── Views/
│   │   ├── MainWindow.axaml               # Primary shell window
│   │   ├── CommandCenterView.axaml        # Main command center layout
│   │   ├── CameraGridWindow.axaml         # Floating multi-camera grid (MPV)
│   │   ├── StaticCameraGridWindow.axaml   # Static snapshot camera grid
│   │   ├── EmbeddedCameraGrid.cs          # Inline grid control
│   │   └── MpvVideoHost.cs                # Native MPV window embedding
│   ├── Application/
│   │   ├── Abstractions/                  # Service interfaces
│   │   └── Services/CameraCatalogService.cs  # Catalog queries
│   ├── Infrastructure/
│   │   ├── Bootstrap/SqliteCameraDataBootstrapper.cs  # Seeds SQLite from JSON on first run
│   │   ├── Persistence/SqliteCameraRepository.cs      # SQLite-backed camera catalog
│   │   ├── Settings/JsonAppSettingsStore.cs            # Persists user settings
│   │   ├── CameraGridManager.cs                        # Grid window lifecycle
│   │   └── LocalStoragePaths.cs                        # App data path helpers
│   └── Services/
│       ├── VlcPlayerService.cs            # LibVLC lifecycle management
│       ├── MpvPlayerService.cs            # MPV player management
│       ├── YtDlpService.cs                # YouTube → direct URL via yt-dlp
│       ├── StaticSnapshotService.cs       # Snapshot polling for static cams
│       ├── ThemeService.cs                # Runtime theme switching
│       └── PerfLog.cs                     # Performance logging
└── Data/                                  # JSON camera databases (editable!)
    ├── traffic-cameras.json               # Houston Transtar + TxDOT cameras
    ├── hls-cameras.json                   # HLS live cameras (left panel)
    ├── live-feeds.json                    # Right panel HLS feeds
    ├── static-camera-groups.json          # Grouped static snapshot cameras
    └── radio-feeds.json                   # Police scanner feeds
```

## Prerequisites (CachyOS / Arch)

### All-in-one install

```bash
# Core dependencies
sudo pacman -S dotnet-sdk vlc mpv yt-dlp

# Fonts for exact UI match (AUR)
paru -S ttf-ibm-plex ttf-rajdhani
```

### 1. .NET 8 SDK

```bash
sudo pacman -S dotnet-sdk
dotnet --version  # verify
```

### 2. VLC + MPV

```bash
sudo pacman -S vlc mpv
```

VLC handles HLS/RTSP streams. MPV is used for the floating camera grid windows via native window embedding.

### 3. yt-dlp (for YouTube live stream extraction)

```bash
sudo pacman -S yt-dlp
```

Only needed for YouTube feeds. All HLS feeds work without it.

### 4. Fonts (optional)

```bash
paru -S ttf-ibm-plex ttf-rajdhani
```

Or download from Google Fonts and drop into `~/.local/share/fonts/`, then run `fc-cache -fv`.

## Build & Run

```bash
cd CoastalCommandCenter

dotnet restore

# Build
dotnet build

# Run
./run.sh
```

For VLC linker diagnostics:

```bash
CCC_ENABLE_LD_DEBUG_LIBS=1 ./run.sh
```

Release build:

```bash
dotnet publish -c Release -r linux-x64 --self-contained
# Output: bin/Release/net8.0/linux-x64/publish/
```

## Adding Cameras

All camera data lives in editable JSON files under `Data/`. On first launch those files seed the local SQLite catalog; after that the app reads from SQLite on startup. To pick up JSON edits, delete the local catalog (`~/.local/share/CoastalCommandCenter/*.db`) and relaunch.

### traffic-cameras.json — Houston Transtar & TxDOT

```json
{
    "id": 100,
    "name": "IH-45 N @ Tidwell",
    "location": "Houston North",
    "lat": 29.8339,
    "lng": -95.3626,
    "camId": 166,
    "type": "transtar"
}
```

### hls-cameras.json / live-feeds.json — HLS Live Streams

```json
{
    "id": 1,
    "name": "28th and Seawall",
    "location": "Galveston",
    "lat": 29.284,
    "lng": -94.805,
    "hlsUrl": "https://...ozolio.com/.../playlist.m3u8"
}
```

### static-camera-groups.json — Snapshot Camera Groups

```json
{
    "id": "highway-6",
    "displayName": "Highway 6",
    "refreshIntervalSeconds": 180,
    "cameras": [
        {
            "id": "1923",
            "name": "Camera 1923",
            "baseSnapshotUrl": "https://www.houstontranstar.org/snapshots/cctv/1923.jpg"
        }
    ]
}
```

## Troubleshooting

**"yt-dlp not found" / YouTube feeds fail**
- `which yt-dlp` and `yt-dlp --version` to verify
- Only YouTube streams require it; HLS feeds are unaffected

**VLC errors or no video**
- `pacman -Qs vlc` should show both `vlc` and `libvlc`
- If video is black but audio plays, try forcing the VLC output module in `VlcPlayerService.cs`: `--vout=xcb_xv` or `--vout=gl`

**MPV camera grid not showing**
- `which mpv` to verify MPV is installed
- MPV windows are embedded via XEmbed — works best under X11/XWayland

**Wayland rendering glitches**
```bash
GDK_BACKEND=x11 ./run.sh
```

**NuGet restore fails**
```bash
dotnet nuget locals all --clear && dotnet restore
```

## Desktop Entry

```bash
cat > ~/.local/share/applications/coastal-command-center.desktop << 'EOF'
[Desktop Entry]
Name=Coastal Command Center
Comment=Texas Traffic & Live Camera Monitor
Exec=/path/to/CoastalCommandCenter
Icon=camera-web
Terminal=false
Type=Application
Categories=Network;Monitor;
EOF
```

Replace `/path/to/CoastalCommandCenter` with the published binary path.
# NativeTrafficCamC-
