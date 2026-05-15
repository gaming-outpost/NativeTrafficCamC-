namespace CoastalCommandCenter.Domain.Cameras;

public enum CameraFeedType
{
    Snapshot,
    Hls,
    YouTube,
    Audio,
    Unknown
}

public enum CameraSourceKind
{
    TrafficCamera,
    LiveTrafficCamera,
    PublicLiveFeed,
    StaticSnapshotCamera
}

public enum CameraGroupType
{
    StaticPlaylist,
    Playlist,
    Category,
    Region,
    Workspace
}

public enum CameraHealthState
{
    Unknown,
    Healthy,
    Degraded,
    Offline
}

public sealed record ProviderRecord
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public string? SourceSystem { get; init; }
    public string? BaseUrl { get; init; }
    public string? Notes { get; init; }
    public string? MetadataJson { get; init; }
    public required DateTime CreatedUtc { get; init; }
    public required DateTime UpdatedUtc { get; init; }
}

public sealed record CameraRecord
{
    public required string Id { get; init; }
    public string? ExternalId { get; init; }
    public required string ProviderId { get; init; }
    public required CameraSourceKind SourceKind { get; init; }
    public required CameraFeedType FeedType { get; init; }
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public string? Region { get; init; }
    public string? Category { get; init; }
    public string? Subgroup { get; init; }
    public string? Location { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? StreamUrl { get; init; }
    public string? SnapshotUrl { get; init; }
    public int? RefreshSeconds { get; init; }
    public bool IsEnabled { get; init; } = true;
    public int SortOrder { get; init; }
    public string? Description { get; init; }
    public string? Notes { get; init; }
    public bool IsFavorite { get; init; }
    public string? CustomDisplayName { get; init; }
    public CameraHealthState HealthState { get; init; } = CameraHealthState.Unknown;
    public string? MetadataJson { get; init; }
    public required DateTime CreatedUtc { get; init; }
    public required DateTime UpdatedUtc { get; init; }
}

public sealed record CameraGroupRecord
{
    public required string Id { get; init; }
    public string? ProviderId { get; init; }
    public required CameraGroupType GroupType { get; init; }
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public string? Region { get; init; }
    public string? Category { get; init; }
    public int? RefreshSeconds { get; init; }
    public bool IsEnabled { get; init; } = true;
    public int SortOrder { get; init; }
    public string? Notes { get; init; }
    public string? MetadataJson { get; init; }
    public required DateTime CreatedUtc { get; init; }
    public required DateTime UpdatedUtc { get; init; }
}

public sealed record CameraGroupMembershipRecord
{
    public required string GroupId { get; init; }
    public required string CameraId { get; init; }
    public string? MembershipRole { get; init; }
    public int SortOrder { get; init; }
    public required DateTime CreatedUtc { get; init; }
}

public sealed record GroupedCameraRecordSet(
    CameraGroupRecord Group,
    IReadOnlyList<CameraRecord> Cameras);

public sealed record CameraCatalogSeed
{
    public IReadOnlyList<ProviderRecord> Providers { get; init; } = [];
    public IReadOnlyList<CameraRecord> Cameras { get; init; } = [];
    public IReadOnlyList<CameraGroupRecord> Groups { get; init; } = [];
    public IReadOnlyList<CameraGroupMembershipRecord> GroupMemberships { get; init; } = [];
}

public sealed record CameraQuery
{
    public IReadOnlyCollection<string> CameraIds { get; init; } = [];
    public IReadOnlyCollection<string> ProviderIds { get; init; } = [];
    public IReadOnlyCollection<string> Regions { get; init; } = [];
    public IReadOnlyCollection<string> GroupIds { get; init; } = [];
    public IReadOnlyCollection<CameraSourceKind> SourceKinds { get; init; } = [];
    public IReadOnlyCollection<CameraFeedType> FeedTypes { get; init; } = [];
    public string? Category { get; init; }
    public bool IncludeDisabled { get; init; }
    public bool OnlyWithCoordinates { get; init; }
}

public sealed record CameraGroupQuery
{
    public IReadOnlyCollection<string> GroupIds { get; init; } = [];
    public IReadOnlyCollection<string> ProviderIds { get; init; } = [];
    public CameraGroupType? GroupType { get; init; }
    public bool IncludeDisabled { get; init; }
}
