using System.Text.Json.Serialization;

namespace CoastalCommandCenter.Models;

public class AppSettings
{
    [JsonPropertyName("selectedLiveFeedIds")]
    public List<string> SelectedLiveFeedIds { get; set; } = [];

    [JsonPropertyName("selectedStaticCameraIds")]
    public List<string> SelectedStaticCameraIds { get; set; } = [];

    [JsonPropertyName("mapLayerMode")]
    public string MapLayerMode { get; set; } = "Map";

    [JsonPropertyName("showStreamHealth")]
    public bool ShowStreamHealth { get; set; }

    // ── Playback tuning (see PlaybackTuning for clamping/validation) ──

    [JsonPropertyName("maxParallelTileLoads")]
    public int MaxParallelTileLoads { get; set; } = 4;

    [JsonPropertyName("vlcNetworkCachingMs")]
    public int VlcNetworkCachingMs { get; set; } = 300;

    [JsonPropertyName("vlcLiveCachingMs")]
    public int VlcLiveCachingMs { get; set; } = 300;

    [JsonPropertyName("vlcHardwareDecode")]
    public bool VlcHardwareDecode { get; set; } = true;

    [JsonPropertyName("vlcWarmPoolSize")]
    public int VlcWarmPoolSize { get; set; } = 4;

    [JsonPropertyName("mpvHardwareDecode")]
    public bool MpvHardwareDecode { get; set; } = true;

    [JsonPropertyName("mpvCacheSecs")]
    public int MpvCacheSecs { get; set; } = 2;
}
