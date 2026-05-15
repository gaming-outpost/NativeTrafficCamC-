using CoastalCommandCenter.Models.Health;

namespace CoastalCommandCenter.Services.Health;

/// <summary>
/// MPV runs as a subprocess fed by yt-dlp; we don't have access to packet-level
/// stats. Health is inferred from process liveness plus an HTTP HEAD against
/// the YouTube watch URL for a coarse latency signal.
/// </summary>
public sealed class MpvStreamHealthProbe : StreamHealthProbeBase
{
    private readonly Func<bool> _isProcessAlive;
    private readonly string? _watchUrl;
    private readonly HttpClient _httpClient;

    public MpvStreamHealthProbe(Func<bool> isProcessAlive, string? watchUrl)
    {
        _isProcessAlive = isProcessAlive;
        _watchUrl = watchUrl;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
    }

    public override async ValueTask<HealthSample?> SampleAsync(CancellationToken ct)
    {
        if (!_isProcessAlive())
        {
            // Treat dead process as a synthetic timeout so the monitor can roll
            // toward Down without crashing on a null sample.
            throw new InvalidOperationException("mpv process is not running");
        }

        double latencyMs = 0;
        if (!string.IsNullOrWhiteSpace(_watchUrl))
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var req = new HttpRequestMessage(HttpMethod.Head, _watchUrl);
                using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                sw.Stop();
                latencyMs = sw.Elapsed.TotalMilliseconds;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                latencyMs = HealthThresholds.MaxHealthyLatencyMs * 2;
            }
        }

        // No packet-level loss data is exposed; if the process is alive we
        // report 0% loss and let the reconnect count drive Unstable.
        return new HealthSample(DateTimeOffset.Now, latencyMs, 0, 0, ReconnectCount);
    }

    protected override void DisposeCore()
    {
        _httpClient.Dispose();
    }
}
