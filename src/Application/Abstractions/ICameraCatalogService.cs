using CoastalCommandCenter.Domain.Cameras;
using CoastalCommandCenter.Models;

namespace CoastalCommandCenter.Application.Abstractions;

public interface ICameraCatalogService
{
    IReadOnlyList<CameraRecord> LoadAllCameras();
    IReadOnlyList<CameraRecord> LoadCamerasByProvider(string providerId);
    IReadOnlyList<CameraRecord> LoadCamerasByRegion(string region);
    IReadOnlyList<CameraRecord> LoadCamerasByFeedType(CameraFeedType feedType);
    IReadOnlyList<CameraRecord> LoadCamerasByGroup(string groupId);
    IReadOnlyList<CameraRecord> LoadMapVisibleCameras();
    IReadOnlyList<HlsCamera> LoadLeftPanelLiveCameras();
    IReadOnlyList<LiveFeed> LoadLiveFeeds();
    IReadOnlyList<TrafficCamera> LoadTrafficCameras();
    IReadOnlyList<StaticCameraGroupDefinition> LoadStaticCameraGroups();
}
