using System.Text.Json;
using CoastalCommandCenter.Domain.Cameras;
using CoastalCommandCenter.Models;

namespace CoastalCommandCenter.Application.Services;

public static class CameraRecordMapper
{
    public static TrafficCamera MapToTrafficCamera(CameraRecord camera)
    {
        return new TrafficCamera
        {
            Id = TryParseInt(camera.ExternalId),
            Name = camera.Name,
            Location = camera.Location ?? camera.Region ?? string.Empty,
            Lat = camera.Latitude ?? 0,
            Lng = camera.Longitude ?? 0,
            CamId = TryGetMetadataInt(camera.MetadataJson, "camId"),
            Type = NormalizeTrafficType(camera),
            TxDotUrl = camera.SnapshotUrl,
            HlsUrl = camera.FeedType == CameraFeedType.Hls ? camera.StreamUrl : null,
            State = TryGetMetadataString(camera.MetadataJson, "state") ?? "TX",
            DbId = camera.Id,
            IsFavorite = camera.IsFavorite,
            CustomDisplayName = camera.CustomDisplayName
        };
    }

    public static HlsCamera MapToHlsCamera(CameraRecord camera)
    {
        return new HlsCamera
        {
            Id = camera.ExternalId ?? camera.Id,
            Name = camera.Name,
            Location = camera.Location ?? camera.Region ?? string.Empty,
            Lat = camera.Latitude ?? 0,
            Lng = camera.Longitude ?? 0,
            HlsUrl = camera.StreamUrl ?? string.Empty,
            State = TryGetMetadataString(camera.MetadataJson, "state") ?? "TX",
            DbId = camera.Id,
            IsFavorite = camera.IsFavorite,
            CustomDisplayName = camera.CustomDisplayName
        };
    }

    public static LiveFeed MapToLiveFeed(CameraRecord camera)
    {
        return new LiveFeed
        {
            Id = camera.ExternalId ?? camera.Id,
            Name = camera.Name,
            City = camera.Region ?? camera.Location ?? "Unknown",
            State = camera.Category ?? "Texas",
            Type = camera.FeedType == CameraFeedType.YouTube ? "youtube" : "hls",
            Url = camera.StreamUrl ?? string.Empty,
            Lat = camera.Latitude,
            Lng = camera.Longitude
        };
    }

    public static int TryParseInt(string? raw)
    {
        return int.TryParse(raw, out var value) ? value : 0;
    }

    public static string NormalizeTrafficType(CameraRecord camera)
    {
        if (!string.IsNullOrWhiteSpace(camera.ProviderId)
            && camera.ProviderId.Contains("txdot", StringComparison.OrdinalIgnoreCase))
        {
            return "txdot";
        }

        return "transtar";
    }

    public static string? TryGetMetadataString(string? metadataJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            if (!document.RootElement.TryGetProperty(propertyName, out var property)
                || property.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var value = property.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    public static int? TryGetMetadataInt(string? metadataJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            if (!document.RootElement.TryGetProperty(propertyName, out var property))
            {
                return null;
            }

            return property.ValueKind switch
            {
                JsonValueKind.Number when property.TryGetInt32(out var number) => number,
                JsonValueKind.String when int.TryParse(property.GetString(), out var parsed) => parsed,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }
}
