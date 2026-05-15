using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CoastalCommandCenter.Models;
using CoastalCommandCenter.Services;

namespace CoastalCommandCenter.ViewModels;

public partial class StaticCameraPreviewViewModel : ObservableObject, IDisposable
{
    private readonly StaticSnapshotService _snapshotService;
    private readonly StaticCameraDefinition _camera;
    private DispatcherTimer? _refreshTimer;
    private CancellationTokenSource? _refreshCts;

    [ObservableProperty] private Bitmap? _snapshotImage;
    [ObservableProperty] private string _lastUpdatedText = "Never";
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private bool _isOnline = true;
    [ObservableProperty] private bool _isLoading;

    public string CameraName => _camera.Name;
    public string CameraId => _camera.Id;

    public StaticCameraPreviewViewModel(
        StaticSnapshotService snapshotService,
        StaticCameraDefinition camera)
    {
        _snapshotService = snapshotService;
        _camera = camera;
    }

    public void StartRefresh(int intervalSeconds = 12)
    {
        if (_refreshTimer != null)
            return;

        _refreshCts = new CancellationTokenSource();
        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(intervalSeconds)
        };
        _refreshTimer.Tick += async (_, _) => await RefreshSnapshot();
        _refreshTimer.Start();

        _ = Task.Run(RefreshSnapshot);
    }

    public void StopRefresh()
    {
        _refreshTimer?.Stop();
        _refreshTimer = null;
        try
        {
            _refreshCts?.Cancel();
        }
        finally
        {
            _refreshCts?.Dispose();
            _refreshCts = null;
        }
    }

    private async Task RefreshSnapshot()
    {
        if (_refreshCts == null || _refreshCts.IsCancellationRequested)
            return;

        try
        {
            IsLoading = true;
            ErrorMessage = string.Empty;

            var ct = _refreshCts?.Token ?? CancellationToken.None;
            if (ct.IsCancellationRequested)
                return;

            var result = await _snapshotService.LoadSnapshotAsync(
                _camera,
                ct);

            SnapshotImage = result.Bitmap;
            LastUpdatedText = result.RetrievedAt.ToString("HH:mm:ss");
            IsOnline = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (HttpRequestException)
        {
            IsOnline = false;
            ErrorMessage = "Connection error";
        }
        catch (Exception)
        {
            IsOnline = false;
            ErrorMessage = "Failed to load snapshot";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void Dispose()
    {
        StopRefresh();
        SnapshotImage?.Dispose();
    }
}
