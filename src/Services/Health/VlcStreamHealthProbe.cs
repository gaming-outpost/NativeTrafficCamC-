using CoastalCommandCenter.Models.Health;
using LibVLCSharp.Shared;

namespace CoastalCommandCenter.Services.Health;

/// <summary>
/// Approximates per-stream health from libvlc's media-level statistics plus an
/// HTTP HEAD against the manifest URL for latency. The tile that owns the
/// player passes a getter so the probe always reads the live MediaPlayer
/// (which can be torn down and recreated on reconnect).
/// </summary>
public sealed class VlcStreamHealthProbe : StreamHealthProbeBase
{
    private readonly Func<MediaPlayer?> _getPlayer;
    private readonly string? _streamUrl;
    private readonly HttpClient _httpClient;

    private long _lastDemuxBytes;
    private DateTimeOffset _lastSampleAt;
    private int _lastDisplayed;
    private int _lastLostPictures;
    private int _lastDemuxCorrupted;

    public VlcStreamHealthProbe(Func<MediaPlayer?> getPlayer, string? streamUrl)
    {
        _getPlayer = getPlayer;
        _streamUrl = streamUrl;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
    }

    public override async ValueTask<HealthSample?> SampleAsync(CancellationToken ct)
    {
        var player = _getPlayer();
        var media = player?.Media;
        if (player is null || media is null)
            return null;

        var stats = media.Statistics;
        var now = DateTimeOffset.Now;

        double latencyMs = 0;
        if (!string.IsNullOrWhiteSpace(_streamUrl))
        {
            latencyMs = await ProbeLatencyAsync(_streamUrl, ct).ConfigureAwait(false);
        }

        // Loss%: lost pictures + demux corrupted, against displayed-since-last-sample.
        int displayedDelta = SafeDelta(stats.DisplayedPictures, _lastDisplayed);
        int lostDelta = SafeDelta(stats.LostPictures, _lastLostPictures);
        int corruptedDelta = SafeDelta(stats.DemuxCorrupted, _lastDemuxCorrupted);
        _lastDisplayed = stats.DisplayedPictures;
        _lastLostPictures = stats.LostPictures;
        _lastDemuxCorrupted = stats.DemuxCorrupted;

        double lossPercent = 0;
        int totalFrames = displayedDelta + lostDelta + corruptedDelta;
        if (totalFrames > 0)
            lossPercent = 100.0 * (lostDelta + corruptedDelta) / totalFrames;

        // Bitrate: bytes-per-second derived from cumulative DemuxReadBytes.
        // Falls back to libvlc's smoothed DemuxBitrate (bytes/s) if delta is unavailable.
        double bitrateKbps;
        long demuxBytes = stats.DemuxReadBytes;
        if (_lastSampleAt != default)
        {
            var elapsed = (now - _lastSampleAt).TotalSeconds;
            if (elapsed > 0 && demuxBytes >= _lastDemuxBytes)
                bitrateKbps = (demuxBytes - _lastDemuxBytes) * 8.0 / 1000.0 / elapsed;
            else
                bitrateKbps = stats.DemuxBitrate * 8.0 / 1000.0;
        }
        else
        {
            bitrateKbps = stats.DemuxBitrate * 8.0 / 1000.0;
        }
        _lastDemuxBytes = demuxBytes;
        _lastSampleAt = now;

        return new HealthSample(now, latencyMs, lossPercent, bitrateKbps, ReconnectCount);
    }

    private async Task<double> ProbeLatencyAsync(string url, CancellationToken ct)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // HEAD failure shouldn't kill the sample — return a sentinel high-latency value.
            return HealthThresholds.MaxHealthyLatencyMs * 2;
        }
    }

    private static int SafeDelta(int current, int previous)
        => current >= previous ? current - previous : current;

    protected override void DisposeCore()
    {
        _httpClient.Dispose();
    }
}
