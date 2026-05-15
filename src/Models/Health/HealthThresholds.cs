namespace CoastalCommandCenter.Models.Health;

public static class HealthThresholds
{
    public const double MaxHealthyLossPercent = 2.0;
    public const double MaxHealthyLatencyMs = 200.0;

    public const int UnstableReconnectsInWindow = 3;
    public const int UnstableWindowSeconds = 60;

    public const int DownConsecutiveTimeouts = 3;

    public const int SampleBufferCapacity = 60;
    public const int EventBufferCapacity = 60;
    public const int SampleIntervalSeconds = 5;
}
