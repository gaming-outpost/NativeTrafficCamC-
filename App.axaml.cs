using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CoastalCommandCenter.Application.Abstractions;
using CoastalCommandCenter.Application.Services;
using CoastalCommandCenter.Infrastructure.Bootstrap;
using CoastalCommandCenter.Infrastructure.Persistence;
using CoastalCommandCenter.Infrastructure.Settings;
using CoastalCommandCenter.Models;
using CoastalCommandCenter.Services;
using CoastalCommandCenter.Services.Health;
using CoastalCommandCenter.ViewModels;
using CoastalCommandCenter.Views;

namespace CoastalCommandCenter;

public partial class App : Avalonia.Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            var appSettingsStore = new JsonAppSettingsStore();
            var settings = appSettingsStore.Load();
            var tuning = PlaybackTuning.FromSettings(settings);

            ICameraDataBootstrapper? cameraBootstrapper = null;
            ICameraRepository? cameraRepository = null;
            ICameraCatalogService cameraCatalogService;
            var isUsingFallbackCatalog = false;
            try
            {
                var sqliteRepository = new SqliteCameraRepository();
                var seedLoader = new LegacyJsonCameraSeedLoader();
                cameraBootstrapper = new SqliteCameraDataBootstrapper(sqliteRepository, seedLoader);
                cameraCatalogService = new CameraCatalogService(sqliteRepository);
                cameraRepository = sqliteRepository;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[App] SQLite bootstrap failed, falling back to legacy JSON camera data: {ex.Message}");
                cameraCatalogService = new LegacyJsonCameraCatalogService(new LegacyJsonCameraSeedLoader());
                isUsingFallbackCatalog = true;
            }

            var vlcService = new VlcPlayerService();
            vlcService.Configure(tuning);
            var ytdlpService = new YtDlpService();
            var mpvService = new MpvPlayerService();
            mpvService.Configure(tuning);
            var staticSnapshotService = new StaticSnapshotService();
            var streamHealthMonitor = new StreamHealthMonitor();

            // Bound concurrent tile-startup work and pre-pay startup costs.
            TileLoadGate.Configure(tuning.MaxParallelTileLoads);
            vlcService.WarmPool(tuning.VlcWarmPoolSize);
            Task.Run(mpvService.WarmBinary);

            // If MainWindow construction throws after services exist, dispose
            // them so we don't leak native handles / processes on a failed start.
            MainViewModel? vm = null;
            try
            {
                vm = new MainViewModel(
                    cameraCatalogService, appSettingsStore, ytdlpService, staticSnapshotService,
                    cameraBootstrapper, cameraRepository, isUsingFallbackCatalog);

                var mainWindow = new MainWindow(vlcService, mpvService, staticSnapshotService, streamHealthMonitor)
                {
                    DataContext = vm
                };

                desktop.MainWindow = mainWindow;
            }
            catch
            {
                try { vm?.Dispose(); } catch { }
                try { vlcService.Dispose(); } catch { }
                try { staticSnapshotService.Dispose(); } catch { }
                try { streamHealthMonitor.Dispose(); } catch { }
                throw;
            }

            var capturedVm = vm;
            desktop.ShutdownRequested += (_, _) =>
            {
                capturedVm.Dispose();
                vlcService.Dispose();
                staticSnapshotService.Dispose();
                streamHealthMonitor.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
