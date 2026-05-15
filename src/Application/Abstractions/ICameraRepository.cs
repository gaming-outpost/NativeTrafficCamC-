using CoastalCommandCenter.Domain.Cameras;

namespace CoastalCommandCenter.Application.Abstractions;

public interface ICameraRepository
{
    IReadOnlyList<CameraRecord> GetAllCameras();
    IReadOnlyList<CameraRecord> GetCameras(CameraQuery query);
    IReadOnlyList<CameraGroupRecord> GetGroups(CameraGroupQuery query);
    IReadOnlyList<GroupedCameraRecordSet> GetGroupsWithCameras(CameraGroupQuery query);
    IReadOnlyList<ProviderRecord> GetProviders();

    void UpsertCamera(CameraRecord camera);
    void UpdateCameraDisplayName(string cameraId, string? customDisplayName);
    void UpdateCameraFavorite(string cameraId, bool isFavorite);
}
