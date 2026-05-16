namespace CoastalCommandCenter.Models;

public enum TileBackend
{
    Auto,
    MpvForYoutube
}

/// <summary>
/// Runtime knobs for tile playback. Built once from <see cref="AppSettings"/>
/// at app startup and threaded into the player services.
/// </summary>
public sealed class PlaybackTuning
{
    public int MaxParallelTileLoads { get; init; } = 4;

    public int VlcNetworkCachingMs { get; init; } = 300;
    public int VlcLiveCachingMs { get; init; } = 300;
    public bool VlcHardwareDecode { get; init; } = true;

    public bool MpvHardwareDecode { get; init; } = true;
    public int MpvCacheSecs { get; init; } = 2;

    /// <summary>How many MediaPlayer instances to pre-allocate at app startup.</summary>
    public int VlcWarmPoolSize { get; init; } = 4;

    public static PlaybackTuning Default { get; } = new();

    public static PlaybackTuning FromSettings(AppSettings s) => new()
    {
        MaxParallelTileLoads = Math.Clamp(s.MaxParallelTileLoads, 1, 16),
        VlcNetworkCachingMs = Math.Clamp(s.VlcNetworkCachingMs, 100, 5000),
        VlcLiveCachingMs = Math.Clamp(s.VlcLiveCachingMs, 100, 5000),
        VlcHardwareDecode = s.VlcHardwareDecode,
        MpvHardwareDecode = s.MpvHardwareDecode,
        MpvCacheSecs = Math.Clamp(s.MpvCacheSecs, 1, 30),
        VlcWarmPoolSize = Math.Clamp(s.VlcWarmPoolSize, 0, 16),
    };
}
