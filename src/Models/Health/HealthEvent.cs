namespace CoastalCommandCenter.Models.Health;

public readonly record struct HealthEvent(
    DateTimeOffset Timestamp,
    HealthEventKind Kind,
    string Detail);
