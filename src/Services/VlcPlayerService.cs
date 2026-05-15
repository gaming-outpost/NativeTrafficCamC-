using LibVLCSharp.Shared;
using CoastalCommandCenter.Models;
using CoastalCommandCenter.Services.Diagnostics;

namespace CoastalCommandCenter.Services;

/// <summary>
/// Manages the single app-wide LibVLC instance and a pool of recyclable MediaPlayers.
/// Pooling avoids the per-tile allocation cost of LibVLCSharp's MediaPlayer ctor;
/// async Parse lets us start playback the instant the network manifest is ready.
/// </summary>
public class VlcPlayerService : IDisposable
{
    public sealed record ActiveCapture(MediaPlayer Player, string NormalizedUrl);

    private LibVLC? _libVlc;
    private readonly object _lock = new();
    private readonly List<MediaPlayer> _activePlayers = [];
    private readonly Dictionary<MediaPlayer, string> _playerUrls = [];
    private readonly Stack<MediaPlayer> _pool = new();
    private PlaybackTuning _tuning = PlaybackTuning.Default;
    private bool _disposed;

    /// <summary>
    /// Override the tuning. Should be called before <see cref="LibVLC"/> is first accessed
    /// so the libvlc-level args reflect the configured caching/HW-decode settings.
    /// </summary>
    public void Configure(PlaybackTuning tuning)
    {
        ArgumentNullException.ThrowIfNull(tuning);
        _tuning = tuning;
    }

    /// <summary>The shared LibVLC instance. Lazily initialized on first access.</summary>
    public LibVLC LibVLC
    {
        get
        {
            if (_libVlc != null) return _libVlc;
            lock (_lock)
            {
                if (_libVlc != null) return _libVlc;
                using var span = TileTiming.Begin("global", "libvlc.create");
                _libVlc = new LibVLC(BuildLibVlcArgs(_tuning));
            }
            return _libVlc;
        }
    }

    private static string[] BuildLibVlcArgs(PlaybackTuning t)
    {
        var args = new List<string>
        {
            "--no-video-title-show",
            $"--network-caching={t.VlcNetworkCachingMs}",
            $"--live-caching={t.VlcLiveCachingMs}",
            "--clock-jitter=0",
            "--clock-synchro=0",
            "--no-osd",
            "--quiet"
        };
        if (t.VlcHardwareDecode)
        {
            // VLC accepts --avcodec-hw=any cross-platform: on Linux it selects
            // VAAPI/VDPAU when available, on Windows D3D11/DXVA2. To verify the
            // selected module fires, run a debug build with --vv and watch for
            // "[avcodec] using <hw> for hardware decoding" in stderr.
            args.Add("--avcodec-hw=any");
        }
        return [.. args];
    }

    /// <summary>
    /// Pre-allocate <paramref name="count"/> MediaPlayers so the first tiles don't
    /// pay the per-player ctor cost. Idempotent — extra calls grow the pool further.
    /// </summary>
    public void WarmPool(int count)
    {
        if (count <= 0 || _disposed) return;

        using var span = TileTiming.Begin("global", $"vlc.warm.pool[{count}]");
        for (int i = 0; i < count; i++)
        {
            var player = new MediaPlayer(LibVLC);
            ConfigureNewPlayer(player);
            lock (_lock) _pool.Push(player);
        }
    }

    private static void ConfigureNewPlayer(MediaPlayer player)
    {
        player.Mute = true;
        try { player.SetVideoTitleDisplay(Position.Disable, 0U); } catch { }
    }

    /// <summary>Pull a MediaPlayer from the pool, or create one if the pool is empty.</summary>
    public MediaPlayer Acquire()
    {
        MediaPlayer? player;
        lock (_lock)
        {
            player = _pool.Count > 0 ? _pool.Pop() : null;
        }

        if (player == null)
        {
            using var span = TileTiming.Begin("global", "vlc.player.new");
            player = new MediaPlayer(LibVLC);
            ConfigureNewPlayer(player);
        }

        lock (_lock) _activePlayers.Add(player);
        return player;
    }

    public MediaPlayer CreatePlayer() => Acquire();

    public MediaPlayer CreateMutedPlayer(bool disableVideoTitle = true)
    {
        var p = Acquire();
        p.Mute = true;
        if (disableVideoTitle)
        {
            try { p.SetVideoTitleDisplay(Position.Disable, 0U); } catch { }
        }
        return p;
    }

    private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "rtsp", "rtp"
    };

    /// <summary>
    /// Construct Media, parse the network manifest asynchronously, then start playback.
    /// Cancellation aborts the parse and never reaches Play(); callers should also
    /// stop/release the player on cancellation.
    /// </summary>
    public async Task PlayStreamAsync(
        MediaPlayer player,
        string url,
        string tileId,
        int? networkCachingMs = null,
        bool suppressVideoTitle = true,
        bool muted = true,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ValidateScheme(url);

        try { player.Stop(); } catch { }

        var uri = new Uri(url, UriKind.Absolute);
        Media media;
        using (TileTiming.Begin(tileId, "vlc.media.create"))
        {
            media = new Media(LibVLC, uri);
            ApplyMediaOptions(media, networkCachingMs, suppressVideoTitle, muted);
        }

        var normalizedUrl = NormalizeStreamUrl(uri.ToString());
        lock (_lock) _playerUrls[player] = normalizedUrl;

        try
        {
            MediaParsedStatus status;
            using (TileTiming.Begin(tileId, "vlc.parse"))
            {
                status = await media.Parse(MediaParseOptions.ParseNetwork, timeout: 5000, cancellationToken: ct)
                    .ConfigureAwait(false);
            }
            TileTiming.Event(tileId, "vlc.parse.status", status.ToString());

            ct.ThrowIfCancellationRequested();

            using (TileTiming.Begin(tileId, "vlc.play.kick"))
            {
                player.Play(media);
            }
        }
        finally
        {
            // Play() retains its own reference to the Media; safe to release ours.
            media.Dispose();
        }
    }

    /// <summary>
    /// Synchronous fire-and-forget for callers that don't await playback startup.
    /// Internally schedules <see cref="PlayStreamAsync"/>.
    /// </summary>
    public void PlayStream(MediaPlayer player, string url, int networkCachingMs = 1000, bool suppressVideoTitle = true)
    {
        _ = PlayStreamAsync(
            player,
            url,
            tileId: "sync",
            networkCachingMs: networkCachingMs,
            suppressVideoTitle: suppressVideoTitle);
    }

    private void ApplyMediaOptions(Media media, int? networkCachingMs, bool suppressVideoTitle, bool muted)
    {
        var netCache = Math.Max(100, networkCachingMs ?? _tuning.VlcNetworkCachingMs);
        media.AddOption($":network-caching={netCache}");
        media.AddOption($":live-caching={_tuning.VlcLiveCachingMs}");
        media.AddOption(":clock-jitter=0");
        media.AddOption(":clock-synchro=0");
        if (muted) media.AddOption(":no-audio");
        if (_tuning.VlcHardwareDecode) media.AddOption(":avcodec-hw=any");
        if (suppressVideoTitle)
        {
            media.AddOption(":no-video-title-show");
            media.AddOption(":input-title-format=");
            media.AddOption(":no-osd");
        }
    }

    private static void ValidateScheme(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !AllowedSchemes.Contains(uri.Scheme))
        {
            var scheme = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Scheme : "invalid";
            throw new ArgumentException(
                $"URL scheme '{scheme}' is not allowed. Only HTTP, HTTPS, RTSP, and RTP are supported.",
                nameof(url));
        }
    }

    public void StopPlayer(MediaPlayer player)
    {
        try
        {
            if (player.IsPlaying) player.Stop();
        }
        catch { }
    }

    public IReadOnlyList<ActiveCapture> GetAllActiveCaptures()
    {
        lock (_lock)
        {
            return _activePlayers
                .Where(player => _playerUrls.TryGetValue(player, out var url) && !string.IsNullOrWhiteSpace(url))
                .Select(player => new ActiveCapture(player, _playerUrls[player]))
                .ToList();
        }
    }

    /// <summary>
    /// Return a player to the pool. Stops playback and clears tracking but does not
    /// dispose — the player is recycled for the next <see cref="Acquire"/>.
    /// </summary>
    public void Release(MediaPlayer player)
    {
        try
        {
            StopPlayer(player);
        }
        catch { }

        lock (_lock)
        {
            _activePlayers.Remove(player);
            _playerUrls.Remove(player);
            if (!_disposed) _pool.Push(player);
        }

        if (_disposed)
        {
            try { player.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Legacy alias — recycles the player back into the pool. Despite the name,
    /// the underlying MediaPlayer is not disposed (that happens in <see cref="StopAll"/>).
    /// </summary>
    public void DestroyPlayer(MediaPlayer player) => Release(player);

    public void StopAll()
    {
        lock (_lock)
        {
            foreach (var p in _activePlayers.ToList())
            {
                try
                {
                    if (p.IsPlaying) p.Stop();
                    p.Dispose();
                }
                catch { }
            }
            _activePlayers.Clear();
            _playerUrls.Clear();

            while (_pool.Count > 0)
            {
                try { _pool.Pop().Dispose(); } catch { }
            }
        }
    }

    private static string NormalizeStreamUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url.Trim();
        }

        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        return builder.Uri.ToString().TrimEnd('/');
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopAll();
        _libVlc?.Dispose();
        _libVlc = null;
        GC.SuppressFinalize(this);
    }
}
