using System.Globalization;
using System.Text;
using System.Text.Json;
using CoastalCommandCenter.Domain.Cameras;
using CoastalCommandCenter.Models;

namespace CoastalCommandCenter.Infrastructure.Bootstrap;

public sealed class LegacyJsonCameraSeedLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly string _dataDirectory;

    public LegacyJsonCameraSeedLoader(string? dataDirectory = null)
    {
        _dataDirectory = dataDirectory ?? Path.Combine(AppContext.BaseDirectory, "Data");
    }

    public CameraCatalogSeed LoadSeed()
    {
        var now = DateTime.UtcNow;
        var trafficCameras = LoadJson<List<TrafficCamera>>("traffic-cameras.json") ?? [];
        var hlsCameras = LoadJson<List<HlsCamera>>("hls-cameras.json") ?? [];
        var liveFeeds = LoadLiveFeeds();
        var staticGroups = LoadStaticCameraGroups();

        var providers = BuildProviders(now);
        var cameras = new List<CameraRecord>();
        var groups = new List<CameraGroupRecord>();
        var memberships = new List<CameraGroupMembershipRecord>();

        for (var i = 0; i < trafficCameras.Count; i++)
        {
            var camera = trafficCameras[i];
            cameras.Add(new CameraRecord
            {
                Id = $"traffic:{camera.Id}",
                ExternalId = camera.Id.ToString(CultureInfo.InvariantCulture),
                ProviderId = camera.Type.Equals("txdot", StringComparison.OrdinalIgnoreCase)
                    ? "txdot"
                    : "houston-transtar",
                SourceKind = CameraSourceKind.TrafficCamera,
                FeedType = camera.IsLive ? CameraFeedType.Hls : CameraFeedType.Snapshot,
                Name = camera.Name,
                DisplayName = camera.Name,
                Region = camera.Location,
                Category = camera.Type,
                Subgroup = camera.IsLive ? "live" : "snapshot",
                Location = camera.Location,
                Latitude = camera.Lat,
                Longitude = camera.Lng,
                StreamUrl = camera.HlsUrl,
                SnapshotUrl = camera.SnapshotUrl,
                RefreshSeconds = camera.IsLive ? null : 180,
                IsEnabled = true,
                SortOrder = i,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    legacySource = "traffic-cameras.json",
                    camId = camera.CamId,
                    legacyType = camera.Type,
                    state = camera.State
                }),
                CreatedUtc = now,
                UpdatedUtc = now
            });
        }

        for (var i = 0; i < hlsCameras.Count; i++)
        {
            var camera = hlsCameras[i];
            cameras.Add(new CameraRecord
            {
                Id = $"hls:{camera.Id}",
                ExternalId = camera.Id,
                ProviderId = "txdot-live",
                SourceKind = CameraSourceKind.LiveTrafficCamera,
                FeedType = CameraFeedType.Hls,
                Name = camera.Name,
                DisplayName = camera.Name,
                Region = camera.Location,
                Category = "traffic-live",
                Subgroup = ExtractLiveTrafficGroup(camera.Id),
                Location = camera.Location,
                Latitude = camera.Lat,
                Longitude = camera.Lng,
                StreamUrl = camera.HlsUrl,
                RefreshSeconds = null,
                IsEnabled = true,
                SortOrder = i,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    legacySource = "hls-cameras.json",
                    state = camera.State
                }),
                CreatedUtc = now,
                UpdatedUtc = now
            });
        }

        for (var i = 0; i < liveFeeds.Count; i++)
        {
            var feed = liveFeeds[i];
            cameras.Add(new CameraRecord
            {
                Id = $"live-feed:{feed.Id}",
                ExternalId = feed.Id,
                ProviderId = "public-live-feed",
                SourceKind = CameraSourceKind.PublicLiveFeed,
                FeedType = feed.IsYoutube ? CameraFeedType.YouTube : CameraFeedType.Hls,
                Name = feed.Name,
                DisplayName = feed.Name,
                Region = feed.City,
                Category = feed.State,
                Subgroup = feed.Type,
                Location = feed.City,
                Latitude = feed.Lat,
                Longitude = feed.Lng,
                StreamUrl = feed.Url,
                RefreshSeconds = null,
                IsEnabled = true,
                SortOrder = i,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    legacySource = "live-feeds.json",
                    legacyType = feed.Type
                }),
                CreatedUtc = now,
                UpdatedUtc = now
            });
        }

        for (var groupIndex = 0; groupIndex < staticGroups.Count; groupIndex++)
        {
            var group = staticGroups[groupIndex];
            groups.Add(new CameraGroupRecord
            {
                Id = group.Id,
                ProviderId = "static-snapshots",
                GroupType = CameraGroupType.StaticPlaylist,
                Name = group.DisplayName,
                DisplayName = group.DisplayName,
                RefreshSeconds = group.EffectiveRefreshIntervalSeconds,
                IsEnabled = true,
                SortOrder = groupIndex,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    legacySource = "static-camera-groups.json"
                }),
                CreatedUtc = now,
                UpdatedUtc = now
            });

            for (var cameraIndex = 0; cameraIndex < group.Cameras.Count; cameraIndex++)
            {
                var camera = group.Cameras[cameraIndex];
                var cameraId = $"static:{group.Id}:{camera.Id}";
                cameras.Add(new CameraRecord
                {
                    Id = cameraId,
                    ExternalId = camera.Id,
                    ProviderId = ResolveStaticProviderId(camera.BaseSnapshotUrl),
                    SourceKind = CameraSourceKind.StaticSnapshotCamera,
                    FeedType = CameraFeedType.Snapshot,
                    Name = camera.Name,
                    DisplayName = camera.Name,
                    Region = string.IsNullOrWhiteSpace(camera.Location)
                        ? group.DisplayName
                        : camera.Location,
                    Category = group.DisplayName,
                    Subgroup = camera.Subgroup,
                    Location = camera.Location,
                    Latitude = camera.Latitude,
                    Longitude = camera.Longitude,
                    SnapshotUrl = camera.BaseSnapshotUrl,
                    RefreshSeconds = group.EffectiveRefreshIntervalSeconds,
                    IsEnabled = true,
                    SortOrder = cameraIndex,
                    MetadataJson = JsonSerializer.Serialize(new
                    {
                        legacySource = "static-camera-groups.json",
                        parentGroupId = group.Id
                    }),
                    CreatedUtc = now,
                    UpdatedUtc = now
                });

                memberships.Add(new CameraGroupMembershipRecord
                {
                    GroupId = group.Id,
                    CameraId = cameraId,
                    SortOrder = cameraIndex,
                    CreatedUtc = now
                });
            }
        }

        return new CameraCatalogSeed
        {
            Providers = providers,
            Cameras = cameras,
            Groups = groups,
            GroupMemberships = memberships
        };
    }

    private List<LiveFeed> LoadLiveFeeds()
    {
        var path = Path.Combine(_dataDirectory, "live-feeds.json");
        if (!File.Exists(path))
        {
            Console.WriteLine($"[LegacyJsonCameraSeedLoader] WARNING: {path} not found");
            return [];
        }

        try
        {
            var json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var feeds = new List<LiveFeed>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var feed = ParseLiveFeed(element);
                if (feed == null)
                {
                    continue;
                }

                var baseId = string.IsNullOrWhiteSpace(feed.Id) ? "feed" : feed.Id;
                var candidate = baseId;
                var suffix = 2;
                while (!seenIds.Add(candidate))
                {
                    candidate = $"{baseId}-{suffix++}";
                }

                feed.Id = candidate;
                feeds.Add(feed);
            }

            return feeds;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LegacyJsonCameraSeedLoader] ERROR loading {path}: {ex.Message}");
            return [];
        }
    }

    private List<StaticCameraGroupDefinition> LoadStaticCameraGroups()
    {
        var groups = LoadJson<List<StaticCameraGroupDefinition>>("static-camera-groups.json") ?? [];
        NormalizeStaticCameraGroups(groups);
        return groups;
    }

    private T? LoadJson<T>(string fileName) where T : class
    {
        var path = Path.Combine(_dataDirectory, fileName);
        if (!File.Exists(path))
        {
            Console.WriteLine($"[LegacyJsonCameraSeedLoader] WARNING: {path} not found");
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LegacyJsonCameraSeedLoader] ERROR loading {path}: {ex.Message}");
            return null;
        }
    }

    private static IReadOnlyList<ProviderRecord> BuildProviders(DateTime nowUtc)
    {
        return
        [
            new ProviderRecord
            {
                Id = "houston-transtar",
                Name = "Houston TranStar",
                Kind = "MunicipalTraffic",
                SourceSystem = "traffic-cameras.json",
                BaseUrl = "https://www.houstontranstar.org/",
                CreatedUtc = nowUtc,
                UpdatedUtc = nowUtc
            },
            new ProviderRecord
            {
                Id = "txdot",
                Name = "Texas Department of Transportation",
                Kind = "StateTraffic",
                SourceSystem = "traffic-cameras.json",
                BaseUrl = "https://www.txdot.gov/",
                CreatedUtc = nowUtc,
                UpdatedUtc = nowUtc
            },
            new ProviderRecord
            {
                Id = "txdot-live",
                Name = "TxDOT Live HLS",
                Kind = "StateTrafficLive",
                SourceSystem = "hls-cameras.json",
                BaseUrl = "https://www.txdot.gov/",
                CreatedUtc = nowUtc,
                UpdatedUtc = nowUtc
            },
            new ProviderRecord
            {
                Id = "public-live-feed",
                Name = "Public Live Feeds",
                Kind = "PublicFeed",
                SourceSystem = "live-feeds.json",
                CreatedUtc = nowUtc,
                UpdatedUtc = nowUtc
            },
            new ProviderRecord
            {
                Id = "static-snapshots",
                Name = "Static Snapshot Views",
                Kind = "StaticSnapshot",
                SourceSystem = "static-camera-groups.json",
                CreatedUtc = nowUtc,
                UpdatedUtc = nowUtc
            }
        ];
    }

    private static string ExtractLiveTrafficGroup(string cameraId)
    {
        var parts = cameraId.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 2 ? parts[1] : "OTHER";
    }

    private static string ResolveStaticProviderId(string snapshotUrl)
    {
        if (snapshotUrl.Contains("houstontranstar", StringComparison.OrdinalIgnoreCase))
        {
            return "houston-transtar";
        }

        if (snapshotUrl.Contains("txdot", StringComparison.OrdinalIgnoreCase))
        {
            return "txdot";
        }

        return "static-snapshots";
    }

    private static LiveFeed? ParseLiveFeed(JsonElement element)
    {
        var id = GetStringOrNumber(element, "id");
        var name = GetString(element, "name");
        var type = GetString(element, "type");
        var url = GetString(element, "url");
        var city = GetString(element, "city");
        var state = GetString(element, "state");
        var hlsUrl = GetString(element, "hlsUrl");
        var location = GetString(element, "location");

        if (string.IsNullOrWhiteSpace(url))
        {
            url = hlsUrl;
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(type))
        {
            type = !string.IsNullOrWhiteSpace(hlsUrl)
                ? "hls"
                : InferFeedType(url);
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            id = !string.IsNullOrWhiteSpace(name)
                ? BuildSlug(name, "feed")
                : Guid.NewGuid().ToString("N");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = id;
        }

        if (string.IsNullOrWhiteSpace(city))
        {
            city = !string.IsNullOrWhiteSpace(location) ? location : "Unknown";
        }

        if (string.IsNullOrWhiteSpace(state))
        {
            state = "Texas";
        }

        return new LiveFeed
        {
            Id = id,
            Name = name,
            Type = type,
            Url = url,
            City = city,
            State = state,
            Lat = GetDouble(element, "lat"),
            Lng = GetDouble(element, "lng")
        };
    }

    private static string InferFeedType(string url)
    {
        if (url.Contains("youtube", StringComparison.OrdinalIgnoreCase)
            || url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            return "youtube";
        }

        return "hls";
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? GetStringOrNumber(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString()?.Trim(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null
        };
    }

    private static double? GetDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var number))
        {
            return number;
        }

        if (property.ValueKind == JsonValueKind.String
            && double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static void NormalizeStaticCameraGroups(List<StaticCameraGroupDefinition> groups)
    {
        var seenGroupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            group.Id = EnsureUniqueSlug(group.Id, group.DisplayName, seenGroupIds, "static-group");
            group.DisplayName = string.IsNullOrWhiteSpace(group.DisplayName)
                ? group.Id
                : group.DisplayName.Trim();

            var seenCameraIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var camera in group.Cameras)
            {
                camera.Id = EnsureUniqueSlug(camera.Id, camera.Name, seenCameraIds, "static-camera");
                camera.Name = string.IsNullOrWhiteSpace(camera.Name)
                    ? camera.Id
                    : camera.Name.Trim();
                camera.Subgroup = camera.Subgroup?.Trim() ?? string.Empty;
                camera.Location = camera.Location?.Trim() ?? string.Empty;
                camera.BaseSnapshotUrl = camera.BaseSnapshotUrl?.Trim() ?? string.Empty;
                camera.ParentGroupId = group.Id;
                camera.ParentGroupDisplayName = group.DisplayName;
            }
        }
    }

    private static string EnsureUniqueSlug(
        string? explicitId,
        string? sourceText,
        HashSet<string> seenIds,
        string fallbackPrefix)
    {
        var baseSlug = BuildSlug(
            !string.IsNullOrWhiteSpace(explicitId) ? explicitId : sourceText,
            fallbackPrefix);

        var candidate = baseSlug;
        var suffix = 2;
        while (!seenIds.Add(candidate))
        {
            candidate = $"{baseSlug}-{suffix++}";
        }

        return candidate;
    }

    private static string BuildSlug(string? input, string fallback)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return fallback;
        }

        var builder = new StringBuilder(input.Length);
        var lastWasDash = false;

        foreach (var character in input.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        var slug = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? fallback : slug;
    }
}
