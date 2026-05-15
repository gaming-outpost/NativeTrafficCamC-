using System.Diagnostics;

namespace CoastalCommandCenter.Services.Diagnostics;

/// <summary>
/// Single-channel structured timing log for tile playback startup.
/// Every line begins with <see cref="Tag"/> so it can be grepped out of stdout:
///   <c>dotnet run | grep '\[tile-timing\]'</c>
/// </summary>
public static class TileTiming
{
    public const string Tag = "[tile-timing]";

    public static void Mark(string tileId, string phase, long elapsedMs, string? extra = null)
    {
        var line = extra is null
            ? $"{Tag} {tileId,-32} {phase,-28} {elapsedMs,6}ms"
            : $"{Tag} {tileId,-32} {phase,-28} {elapsedMs,6}ms  {extra}";
        Console.WriteLine(line);
    }

    public static void Event(string tileId, string phase, string? extra = null)
    {
        var line = extra is null
            ? $"{Tag} {tileId,-32} {phase,-28}    --     event"
            : $"{Tag} {tileId,-32} {phase,-28}    --     event  {extra}";
        Console.WriteLine(line);
    }

    public static Span Begin(string tileId, string phase) => new(tileId, phase);

    public readonly struct Span : IDisposable
    {
        private readonly string _tileId;
        private readonly string _phase;
        private readonly long _startTicks;

        internal Span(string tileId, string phase)
        {
            _tileId = tileId;
            _phase = phase;
            _startTicks = Stopwatch.GetTimestamp();
        }

        public long ElapsedMs => (long)Stopwatch.GetElapsedTime(_startTicks).TotalMilliseconds;

        public void Dispose() => Mark(_tileId, _phase, ElapsedMs);
    }
}
