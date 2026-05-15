
using System.Text.Json.Serialization;

namespace CoastalCommandCenter.Models;

public sealed class StaticCameraDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("group")]
    public string Subgroup { get; set; } = "";

    [JsonPropertyName("location")]
    public string Location { get; set; } = "";

    [JsonPropertyName("latitude")]
    public double? Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double? Longitude { get; set; }

    [JsonPropertyName("baseSnapshotUrl")]
    public string BaseSnapshotUrl { get; set; } = "";

    [JsonIgnore]
    public string ParentGroupId { get; set; } = "";

    [JsonIgnore]
    public string ParentGroupDisplayName { get; set; } = "";

    [JsonIgnore]
    public bool HasLocation => !string.IsNullOrWhiteSpace(Location);

    [JsonIgnore]
    public bool HasSubgroup => !string.IsNullOrWhiteSpace(Subgroup);

    [JsonIgnore]
    public bool HasCoordinates => Latitude.HasValue && Longitude.HasValue;

    [JsonIgnore]
    public string StableKey => string.IsNullOrWhiteSpace(ParentGroupId)
        ? Id
        : $"{ParentGroupId}:{Id}";
}

public sealed class StaticCameraGroupDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("refreshIntervalSeconds")]
    public int? RefreshIntervalSeconds { get; set; }

    [JsonPropertyName("cameras")]
    public List<StaticCameraDefinition> Cameras { get; set; } = [];

    [JsonIgnore]
    public int EffectiveRefreshIntervalSeconds
    {
        get
        {
            var interval = RefreshIntervalSeconds.GetValueOrDefault(180);
            return interval > 0 ? interval : 180;
        }
    }
}
