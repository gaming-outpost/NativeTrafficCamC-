using System.Text;
using System.Text.Json.Serialization;

namespace CoastalCommandCenter.Models;

/// <summary>
/// A traffic camera (Houston Transtar snapshot or TxDOT viewer).
/// Loaded from traffic-cameras.json.
/// </summary>
public class TrafficCamera
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("location")]
    public string Location { get; set; } = "";

    [JsonPropertyName("lat")]
    public double Lat { get; set; }

    [JsonPropertyName("lng")]
    public double Lng { get; set; }

    [JsonPropertyName("camId")]
    public int? CamId { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "transtar";  // "transtar" or "txdot"

    [JsonPropertyName("state")]
    public string State { get; set; } = "TX";

    [JsonPropertyName("txdotUrl")]
    public string? TxDotUrl { get; set; }

    /// <summary>
    /// Optional live HLS stream URL (.m3u8). When present the camera doubles as a
    /// live traffic camera — clicking it plays video instead of showing a snapshot.
    /// </summary>
    [JsonPropertyName("hlsUrl")]
    public string? HlsUrl { get; set; }

    /// <summary>True when this camera has a live HLS stream attached.</summary>
    [JsonIgnore]
    public bool IsLive => !string.IsNullOrWhiteSpace(HlsUrl);

    [JsonIgnore] public string DbId { get; set; } = "";
    [JsonIgnore] public bool IsFavorite { get; set; }
    [JsonIgnore] public string? CustomDisplayName { get; set; }

    /// <summary>Snapshot URL built from camId for Transtar cameras.</summary>
    public string SnapshotUrl => Type == "transtar" && CamId.HasValue
        ? $"https://traffic.houstontranstar.org/layers/gc.aspx?cam={CamId}&loc={Uri.EscapeDataString(Name)}&fr=1&ps=0"
        : TxDotUrl ?? "";
}

/// <summary>
/// An HLS live camera (left panel, live feed mode).
/// Loaded from hls-cameras.json.
/// </summary>
public class HlsCamera
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("location")]
    public string Location { get; set; } = "";

    [JsonPropertyName("lat")]
    public double Lat { get; set; }

    [JsonPropertyName("lng")]
    public double Lng { get; set; }

    [JsonPropertyName("hlsUrl")]
    public string HlsUrl { get; set; } = "";

    [JsonPropertyName("state")]
    public string State { get; set; } = "TX";

    [JsonIgnore] public string DbId { get; set; } = "";
    [JsonIgnore] public bool IsFavorite { get; set; }
    [JsonIgnore] public string? CustomDisplayName { get; set; }
}

/// <summary>
/// A right-panel live feed (YouTube or HLS).
/// Loaded from live-feeds.json.
/// </summary>
public class LiveFeed
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("city")]
    public string City { get; set; } = "";

    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "hls";  // "youtube" or "hls"

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("lat")]
    public double? Lat { get; set; }

    [JsonPropertyName("lng")]
    public double? Lng { get; set; }

    /// <summary>True if this is a YouTube feed needing yt-dlp extraction.</summary>
    [JsonIgnore]
    public bool IsYoutube => Type.Equals("youtube", StringComparison.OrdinalIgnoreCase);

    /// <summary>True if this is a direct HLS feed.</summary>
    [JsonIgnore]
    public bool IsHls => Type.Equals("hls", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Union type for anything selectable as a "camera" in the left panel.
/// Wraps traffic cams, HLS cams, and live feeds uniformly.
/// </summary>
public class CameraItem
{
    private const string TranstarSnapshotBaseUrl = "https://www.houstontranstar.org/snapshots/cctv/";

    public string UniqueId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Location { get; set; } = "";
    public double Lat { get; set; }
    public double Lng { get; set; }
    public CameraKind Kind { get; set; }

    // Only one of these is set depending on Kind
    public string? StreamUrl { get; set; }      // HLS .m3u8 or Transtar snapshot URL
    public string? YoutubeUrl { get; set; }      // YouTube embed URL (needs yt-dlp)
    public int? TranstarCamId { get; set; }
    public string? TxDotUrl { get; set; }
    public CameraSource Source { get; set; }
    public string State { get; set; } = "TX";

    /// <summary>SQLite primary key (CameraRecord.Id). Used for database updates.</summary>
    public string DbId { get; set; } = "";
    public bool IsFavorite { get; set; }
    public string? CustomDisplayName { get; set; }

    /// <summary>User-visible name: CustomDisplayName if set, otherwise Name.</summary>
    public string DisplayLabel => string.IsNullOrWhiteSpace(CustomDisplayName) ? Name : CustomDisplayName;

    public bool IsLive => Kind == CameraKind.Hls || Kind == CameraKind.Youtube;

    /// <summary>
    /// Display group key for the left-panel grouped list.
    /// HLS cameras: 3-letter city code from UniqueId (e.g. "hls-TX_HOU_005" → "HOU").
    /// Traffic cameras: road name before the first '@' in Name (e.g. "IH-45 Gulf @ FM-646" → "IH-45 GULF").
    /// </summary>
    public string GroupKey
    {
        get
        {
            if (Source == CameraSource.LeftHls)
            {
                // UniqueId format: "hls-TX_HOU_005" → split on '_', take the city segment
                var parts = UniqueId.Split('_');
                return parts.Length >= 2 ? parts[1] : "OTHER";
            }

            // Traffic cameras: group by road name (text before first '@')
            var atIndex = Name.IndexOf('@', StringComparison.Ordinal);
            var road = atIndex > 0 ? Name[..atIndex].Trim() : Name.Trim();
            // Normalize: strip trailing direction words to keep groups tighter
            return road.ToUpperInvariant();
        }
    }

    /// <summary>
    /// Returns true if the URL uses an allowed scheme (http or https).
    /// </summary>
    public static bool IsAllowedUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Build a snapshot URL for static traffic cameras and inject a cache-busting arg timestamp.
    /// Prefers the TranStar camId pattern when available, otherwise reuses the existing URL.
    /// </summary>
    public string? BuildSnapshotUrl(long utcUnixMilliseconds)
    {
        string? baseUrl = null;

        if (TranstarCamId.HasValue)
        {
            baseUrl = $"{TranstarSnapshotBaseUrl}{TranstarCamId.Value}.jpg";
        }
        else if (!string.IsNullOrWhiteSpace(StreamUrl))
        {
            baseUrl = StreamUrl;
        }
        else if (!string.IsNullOrWhiteSpace(TxDotUrl))
        {
            baseUrl = TxDotUrl;
        }

        if (string.IsNullOrWhiteSpace(baseUrl) || !IsAllowedUrl(baseUrl))
        {
            return null;
        }

        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var absoluteUri))
        {
            var queryParts = absoluteUri.Query
                .TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(part => !part.StartsWith("arg=", StringComparison.OrdinalIgnoreCase))
                .ToList();
            queryParts.Add($"arg={utcUnixMilliseconds}");

            var uriBuilder = new UriBuilder(absoluteUri)
            {
                Query = string.Join("&", queryParts)
            };
            return uriBuilder.Uri.ToString();
        }

        var separator = baseUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var builder = new StringBuilder(baseUrl.Length + 32);
        builder.Append(baseUrl);
        builder.Append(separator);
        builder.Append("arg=");
        builder.Append(utcUnixMilliseconds);
        return builder.ToString();
    }

    /// <summary>Creates a <see cref="CameraItem"/> from a traffic camera JSON entry.</summary>
    public static CameraItem FromTraffic(TrafficCamera cam) => new()
    {
        UniqueId = $"traffic-{cam.Id}",
        Name = cam.Name,
        Location = cam.Location,
        Lat = cam.Lat,
        Lng = cam.Lng,
        // When an hlsUrl is present the entry is a live traffic camera.
        // TranstarCamId is still stored so the map popup can show the snapshot.
        Kind = cam.IsLive   ? CameraKind.Hls
             : cam.Type == "txdot" ? CameraKind.TxDot
             : CameraKind.Transtar,
        Source = CameraSource.LeftTraffic,
        TranstarCamId = cam.CamId,
        TxDotUrl = cam.TxDotUrl,
        StreamUrl = cam.IsLive ? cam.HlsUrl : cam.SnapshotUrl,
        State = cam.State,
        DbId = cam.DbId,
        IsFavorite = cam.IsFavorite,
        CustomDisplayName = cam.CustomDisplayName
    };

    /// <summary>Creates a <see cref="CameraItem"/> from an HLS camera JSON entry.</summary>
    public static CameraItem FromHls(HlsCamera cam) => new()
    {
        UniqueId = $"hls-{cam.Id}",
        Name = cam.Name,
        Location = cam.Location,
        Lat = cam.Lat,
        Lng = cam.Lng,
        Kind = CameraKind.Hls,
        Source = CameraSource.LeftHls,
        StreamUrl = cam.HlsUrl,
        State = cam.State,
        DbId = cam.DbId,
        IsFavorite = cam.IsFavorite,
        CustomDisplayName = cam.CustomDisplayName
    };

    /// <summary>Creates a <see cref="CameraItem"/> from a live feed JSON entry.</summary>
    public static CameraItem FromLiveFeed(LiveFeed feed) => new()
    {
        UniqueId = feed.Id,
        Name = feed.Name,
        Location = string.IsNullOrWhiteSpace(feed.City) ? feed.Type.ToUpperInvariant() : feed.City,
        Lat = feed.Lat ?? 0,
        Lng = feed.Lng ?? 0,
        Kind = feed.IsYoutube ? CameraKind.Youtube : CameraKind.Hls,
        Source = CameraSource.RightFeed,
        StreamUrl = feed.IsYoutube ? null : feed.Url,
        YoutubeUrl = feed.IsYoutube ? feed.Url : null,
        State = feed.State
    };
}

public enum CameraKind
{
    Transtar,
    TxDot,
    Hls,
    Youtube
}

public enum CameraSource
{
    LeftTraffic,
    LeftHls,
    RightFeed
}
