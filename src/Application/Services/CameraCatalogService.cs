using CoastalCommandCenter.Application.Abstractions;
using CoastalCommandCenter.Domain.Cameras;
using CoastalCommandCenter.Models;
using static CoastalCommandCenter.Application.Services.CameraRecordMapper;

namespace CoastalCommandCenter.Application.Services;

public sealed class CameraCatalogService : ICameraCatalogService
{
    private readonly ICameraRepository _repository;

    public CameraCatalogService(ICameraRepository repository)
    {
        _repository = repository;
    }

    public IReadOnlyList<CameraRecord> LoadAllCameras() => _repository.GetAllCameras();

    public IReadOnlyList<CameraRecord> LoadCamerasByProvider(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return [];
        }

        return _repository.GetCameras(new CameraQuery
        {
            ProviderIds = [providerId.Trim()]
        });
    }

    public IReadOnlyList<CameraRecord> LoadCamerasByRegion(string region)
    {
        if (string.IsNullOrWhiteSpace(region))
        {
            return [];
        }

        return _repository.GetCameras(new CameraQuery
        {
            Regions = [region.Trim()]
        });
    }

    public IReadOnlyList<CameraRecord> LoadCamerasByFeedType(CameraFeedType feedType)
    {
        return _repository.GetCameras(new CameraQuery
        {
            FeedTypes = [feedType]
        });
    }

    public IReadOnlyList<CameraRecord> LoadCamerasByGroup(string groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return [];
        }

        return _repository.GetCameras(new CameraQuery
        {
            GroupIds = [groupId.Trim()]
        });
    }

    public IReadOnlyList<CameraRecord> LoadMapVisibleCameras()
    {
        return _repository.GetCameras(new CameraQuery
        {
            SourceKinds =
            [
                CameraSourceKind.TrafficCamera,
                CameraSourceKind.LiveTrafficCamera,
                CameraSourceKind.PublicLiveFeed
            ],
            OnlyWithCoordinates = true
        });
    }

    public IReadOnlyList<HlsCamera> LoadLeftPanelLiveCameras()
    {
        return _repository.GetCameras(new CameraQuery
            {
                SourceKinds = [CameraSourceKind.LiveTrafficCamera],
                FeedTypes = [CameraFeedType.Hls]
            })
            .Select(MapToHlsCamera)
            .ToList();
    }

    public IReadOnlyList<LiveFeed> LoadLiveFeeds()
    {
        return _repository.GetCameras(new CameraQuery
            {
                SourceKinds = [CameraSourceKind.PublicLiveFeed]
            })
            .Select(MapToLiveFeed)
            .ToList();
    }

    public IReadOnlyList<TrafficCamera> LoadTrafficCameras()
    {
        return _repository.GetCameras(new CameraQuery
            {
                SourceKinds = [CameraSourceKind.TrafficCamera]
            })
            .Select(MapToTrafficCamera)
            .ToList();
    }

    public IReadOnlyList<StaticCameraGroupDefinition> LoadStaticCameraGroups()
    {
        return _repository.GetGroupsWithCameras(new CameraGroupQuery
            {
                GroupType = CameraGroupType.StaticPlaylist
            })
            .Select(MapToStaticCameraGroup)
            .ToList();
    }

    private static StaticCameraGroupDefinition MapToStaticCameraGroup(GroupedCameraRecordSet groupedSet)
    {
        return new StaticCameraGroupDefinition
        {
            Id = groupedSet.Group.Id,
            DisplayName = groupedSet.Group.DisplayName,
            RefreshIntervalSeconds = groupedSet.Group.RefreshSeconds,
            Cameras = groupedSet.Cameras
                .Select(camera => new StaticCameraDefinition
                {
                    Id = camera.ExternalId ?? camera.Id,
                    Name = camera.DisplayName,
                    Subgroup = camera.Subgroup ?? string.Empty,
                    Location = camera.Location ?? string.Empty,
                    Latitude = camera.Latitude,
                    Longitude = camera.Longitude,
                    BaseSnapshotUrl = camera.SnapshotUrl ?? string.Empty,
                    ParentGroupId = groupedSet.Group.Id,
                    ParentGroupDisplayName = groupedSet.Group.DisplayName
                })
                .ToList()
        };
    }

}
