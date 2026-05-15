namespace CoastalCommandCenter.Models;

public enum StreamCaptureBackend
{
    Vlc,
    Mpv
}

public sealed record VisibleStreamCaptureRequest(
    string StreamId,
    string Region,
    string CameraName,
    StreamCaptureBackend Backend,
    string? CaptureUrl,
    string? MpvFeedId);
