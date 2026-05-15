using CoastalCommandCenter.Application.Abstractions;
using CoastalCommandCenter.Domain.Cameras;
using CoastalCommandCenter.Models;
using static CoastalCommandCenter.Application.Services.CameraRecordMapper;

namespace CoastalCommandCenter.Infrastructure.Bootstrap;

public sealed class LegacyJsonCameraCatalogService : ICameraCatalogService
{
    private readonly LegacyJsonCameraSeedLoader _seedLoader;
    private CameraCatalogSeed? _cachedSeed;

    public LegacyJsonCameraCatalogService(LegacyJsonCameraSeedLoader seedLoader)
    {
        _seedLoader = seedLoader;
    }

    public IReadOnlyList<CameraRecord> LoadAllCameras() => Seed.Cameras;

    public IReadOnlyList<CameraRecord> LoadCamerasByProvider(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return [];
        }

        return LoadAllCameras()
            .Where(camera => string.Equals(camera.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public IReadOnlyList<CameraRecord> LoadCamerasByRegion(string region)
    {
        if (string.IsNullOrWhiteSpace(region))
        {
            return [];
        }

        return LoadAllCameras()
            .Where(camera => string.Equals(camera.Region, region, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public IReadOnlyList<CameraRecord> LoadCamerasByFeedType(CameraFeedType feedType)
    {
        return LoadAllCameras()
            .Where(camera => camera.FeedType == feedType)
            .ToList();
    }

    public IReadOnlyList<CameraRecord> LoadCamerasByGroup(string groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return [];
        }

        var cameraIds = Seed.GroupMemberships
            .Where(membership => string.Equals(membership.GroupId, groupId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(membership => membership.SortOrder)
            .Select(membership => membership.CameraId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return Seed.Cameras
            .Where(camera => cameraIds.Contains(camera.Id))
            .ToList();
    }

    public IReadOnlyList<CameraRecord> LoadMapVisibleCameras()
    {
        return LoadAllCameras()
            .Where(camera => camera.SourceKind != CameraSourceKind.StaticSnapshotCamera)
            .Where(camera => camera.Latitude.HasValue && camera.Longitude.HasValue)
            .ToList();
    }

    public IReadOnlyList<HlsCamera> LoadLeftPanelLiveCameras()
    {
        return Seed.Cameras
            .Where(camera => camera.SourceKind == CameraSourceKind.LiveTrafficCamera)
            .Where(camera => camera.FeedType == CameraFeedType.Hls)
            .Select(MapToHlsCamera)
            .ToList();
    }

    public IReadOnlyList<LiveFeed> LoadLiveFeeds()
    {
        return Seed.Cameras
            .Where(camera => camera.SourceKind == CameraSourceKind.PublicLiveFeed)
            .Select(MapToLiveFeed)
            .ToList();
    }

    public IReadOnlyList<TrafficCamera> LoadTrafficCameras()
    {
        return Seed.Cameras
            .Where(camera => camera.SourceKind == CameraSourceKind.TrafficCamera)
            .Select(MapToTrafficCamera)
            .ToList();
    }

    public IReadOnlyList<StaticCameraGroupDefinition> LoadStaticCameraGroups()
    {
        var staticGroups = Seed.Groups
            .Where(group => group.GroupType == CameraGroupType.StaticPlaylist)
            .OrderBy(group => group.SortOrder)
            .ToList();

        var membershipLookup = Seed.GroupMemberships
            .GroupBy(membership => membership.GroupId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(item => item.SortOrder).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var cameraLookup = Seed.Cameras.ToDictionary(camera => camera.Id, StringComparer.OrdinalIgnoreCase);

        var result = new List<StaticCameraGroupDefinition>(staticGroups.Count);
        foreach (var group in staticGroups)
        {
            membershipLookup.TryGetValue(group.Id, out var memberships);
            var cameras = memberships ?? [];

            result.Add(new StaticCameraGroupDefinition
            {
                Id = group.Id,
                DisplayName = group.DisplayName,
                RefreshIntervalSeconds = group.RefreshSeconds,
                Cameras = cameras
                    .Where(membership => cameraLookup.ContainsKey(membership.CameraId))
                    .Select(membership => cameraLookup[membership.CameraId])
                    .Select(camera => new StaticCameraDefinition
                    {
                        Id = camera.ExternalId ?? camera.Id,
                        Name = camera.DisplayName,
                        Subgroup = camera.Subgroup ?? string.Empty,
                        Location = camera.Location ?? string.Empty,
                        Latitude = camera.Latitude,
                        Longitude = camera.Longitude,
                        BaseSnapshotUrl = camera.SnapshotUrl ?? string.Empty,
                        ParentGroupId = group.Id,
                        ParentGroupDisplayName = group.DisplayName
                    })
                    .ToList()
            });
        }

        return result;
    }

    private CameraCatalogSeed Seed => _cachedSeed ??= _seedLoader.LoadSeed();

}
