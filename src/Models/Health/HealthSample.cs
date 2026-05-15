namespace CoastalCommandCenter.Models.Health;

public readonly record struct HealthSample(
    DateTimeOffset Timestamp,
    double LatencyMs,
    double LossPercent,
    double BitrateKbps,
    int ReconnectCount);
