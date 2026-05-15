using Avalonia.Media;
using CoastalCommandCenter.Models.Health;
using CoastalCommandCenter.Services;

namespace CoastalCommandCenter.Services.Health;

/// <summary>
/// Maps a <see cref="HealthClassification"/> to an existing themed brush
/// so the badge colors stay consistent with the rest of the app.
/// </summary>
public static class HealthBrushes
{
    public static IBrush ForClassification(HealthClassification classification) => classification switch
    {
        HealthClassification.Healthy  => ThemeService.GetBrush("BrushLiveDot"),
        HealthClassification.Degraded => ThemeService.GetBrush("BrushWarnDot"),
        HealthClassification.Unstable => ThemeService.GetBrush("BrushWarnDot"),
        HealthClassification.Down     => ThemeService.GetBrush("BrushDangerSecondary"),
        _ => ThemeService.GetBrush("BrushOffDot")
    };

    public static string SummaryFor(HealthSample sample, int reconnectCount)
        => $"Latency {sample.LatencyMs:0} ms | Loss {sample.LossPercent:0.0}% | {reconnectCount} reconnect{(reconnectCount == 1 ? "" : "s")}";
}
