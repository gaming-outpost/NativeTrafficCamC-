using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CoastalCommandCenter.Models;
using CoastalCommandCenter.Services.Diagnostics;

namespace CoastalCommandCenter.Services;

/// <summary>
/// Manages mpv subprocesses for YouTube feed playback.
/// Each feed gets its own mpv process embedded into a native X11 window via --wid.
/// Subsequent URL changes for the same feed are dispatched via the IPC socket
/// (<c>loadfile … replace</c>) instead of restarting the process.
/// </summary>
public sealed class MpvPlayerService
{
    private sealed record ActiveSession(Process Process, string IpcPath, nint WindowId);

    private const string MpvBinaryPath = "/usr/bin/mpv";

    // Key: SessionKey(feedId, windowId) — one entry per (feed, tile-window) pair so that
    // two tiles showing the same feed in different windows don't stomp each other's sessions.
    private readonly ConcurrentDictionary<string, ActiveSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private PlaybackTuning _tuning = PlaybackTuning.Default;

    private static string SessionKey(string feedId, nint windowId) =>
        $"{feedId}\x00{windowId:X}";

    public void Configure(PlaybackTuning tuning)
    {
        ArgumentNullException.ThrowIfNull(tuning);
        _tuning = tuning;
    }

    /// <summary>
    /// Pages in the mpv binary and its shared libraries by running <c>mpv --version</c>.
    /// Cheap (a few ms), fire-and-forget at app startup so the first real spawn doesn't
    /// pay disk-fetch cost. Limit of subprocess embedding: there's no equivalent of
    /// libmpv's "create a handle, hand it a window later" — we still pay process spawn
    /// per feed, but at least the kernel page cache is warm.
    /// </summary>
    public void WarmBinary()
    {
        using var span = TileTiming.Begin("global", "mpv.warm.binary");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = MpvBinaryPath,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(500);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[mpv] WarmBinary failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Spawns an mpv process for <paramref name="feedId"/> and embeds it into
    /// <paramref name="windowId"/>. If a session already exists for this feedId,
    /// reuses it via IPC <c>loadfile … replace</c> (no process restart).
    /// </summary>
    public void Start(string feedId, nint windowId, string url)
    {
        if (windowId == 0)
        {
            Console.WriteLine($"[mpv] Skipping {feedId}: native window handle is 0.");
            return;
        }

        var key = SessionKey(feedId, windowId);

        // Reuse path: same feed, still alive, same window — just swap the URL.
        if (_sessions.TryGetValue(key, out var existing) && IsAlive(existing))
        {
            _ = LoadFileAsync(existing, url, feedId);
            return;
        }

        Stop(feedId, windowId);

        Console.WriteLine($"[mpv] Embedding into X11 window 0x{windowId:X} ({windowId}) for {feedId}");
        Console.WriteLine($"[mpv] URL: {url}");

        var ipcPath = BuildIpcPath();
        var psi = CreateStartInfo(windowId, url, ipcPath, _tuning);

        try
        {
            using var span = TileTiming.Begin(feedId, "mpv.process.start");

            // Build the Process object and wire Exited *before* Start(), so a
            // crash in the first few ms still fires the cleanup handler.
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.Exited += (_, _) => OnProcessExited(feedId, key, ipcPath);

            if (!process.Start())
            {
                process.Dispose();
                return;
            }

            _sessions[key] = new ActiveSession(process, ipcPath, windowId);
            Console.WriteLine($"[mpv] Started PID {process.Id} for {feedId}");
            BeginLogging(process, feedId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[mpv] Failed to start for {feedId}: {ex.Message}");
        }
    }

    private void OnProcessExited(string feedId, string sessionKey, string ipcPath)
    {
        Console.WriteLine($"[mpv] PID for {feedId} exited.");
        if (_sessions.TryRemove(sessionKey, out var session))
        {
            try { session.Process.Dispose(); } catch { }
            TryDeleteIpcSocket(session.IpcPath);
        }
        else
        {
            TryDeleteIpcSocket(ipcPath);
        }
    }

    /// <summary>
    /// Swap the URL of an already-running session, or fall back to <see cref="Start"/>
    /// if there is none. This is the fast path for tile refresh — no process churn.
    /// </summary>
    public void LoadUrl(string feedId, nint windowId, string url)
    {
        var key = SessionKey(feedId, windowId);
        if (_sessions.TryGetValue(key, out var existing) && IsAlive(existing))
        {
            _ = LoadFileAsync(existing, url, feedId);
            return;
        }

        Start(feedId, windowId, url);
    }

    private static bool IsAlive(ActiveSession s)
    {
        try { return !s.Process.HasExited; }
        catch { return false; }
    }

    private static ProcessStartInfo CreateStartInfo(nint windowId, string url, string ipcPath, PlaybackTuning t)
    {
        var psi = new ProcessStartInfo
        {
            FileName = MpvBinaryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Force X11 mode so --wid embedding works (Wayland doesn't support --wid).
        psi.Environment["WAYLAND_DISPLAY"] = "";
        psi.Environment["DISPLAY"] = Environment.GetEnvironmentVariable("DISPLAY") ?? ":0";

        psi.ArgumentList.Add($"--wid={windowId}");
        psi.ArgumentList.Add("--vo=gpu");
        psi.ArgumentList.Add("--gpu-context=x11egl");
        psi.ArgumentList.Add("--mute");
        psi.ArgumentList.Add("--osd-level=0");
        psi.ArgumentList.Add("--no-input-default-bindings");
        psi.ArgumentList.Add("--no-input-cursor");
        psi.ArgumentList.Add($"--input-ipc-server={ipcPath}");

        // Low-latency tuning. profile=low-latency sets a bundle of options that
        // mpv picked for live streaming; the explicit flags below augment it.
        psi.ArgumentList.Add("--profile=low-latency");
        psi.ArgumentList.Add("--cache=yes");
        psi.ArgumentList.Add($"--cache-secs={t.MpvCacheSecs}");
        psi.ArgumentList.Add("--demuxer-lavf-o=fflags=+nobuffer");
        psi.ArgumentList.Add("--vd-lavc-threads=0");

        if (t.MpvHardwareDecode)
        {
            psi.ArgumentList.Add("--hwdec=auto-safe");
        }

        // Note: we cannot set ytdl=no — YouTube URL resolution depends on mpv's
        // built-in ytdl_hook + the --ytdl-format selector below.
        psi.ArgumentList.Add("--ytdl-format=best[protocol=m3u8][height<=480]/best[height<=480]");
        psi.ArgumentList.Add("--ytdl-raw-options=cookies-from-browser=brave");
        psi.ArgumentList.Add(url);

        return psi;
    }

    private static void BeginLogging(Process process, string feedId)
    {
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                Console.WriteLine($"[mpv/{feedId}] {e.Data}");
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                Console.WriteLine($"[mpv/{feedId}] {e.Data}");
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    /// <summary>Kills the mpv process for the given feed embedded in <paramref name="windowId"/>.</summary>
    public void Stop(string feedId, nint windowId)
    {
        var key = SessionKey(feedId, windowId);
        if (_sessions.TryRemove(key, out var session))
        {
            try
            {
                if (!session.Process.HasExited)
                {
                    session.Process.Kill();
                    Console.WriteLine($"[mpv] Killed PID {session.Process.Id} for {feedId}");
                }
            }
            catch { /* best-effort */ }
            finally
            {
                session.Process.Dispose();
                TryDeleteIpcSocket(session.IpcPath);
            }
        }
    }

    public async Task<bool> CaptureFrameAsync(string feedId, string filePath, CancellationToken ct = default)
    {
        var (sessionKey, session) = FindByFeedId(feedId);
        if (session == null) return false;

        if (session.Process.HasExited)
        {
            _sessions.TryRemove(sessionKey, out _);
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? AppContext.BaseDirectory);

        try
        {
            await WaitForIpcSocketAsync(session.IpcPath, ct);
            return await SendScreenshotCommandAsync(session.IpcPath, filePath, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[mpv] Snapshot failed for {feedId}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Returns true while the mpv subprocess for this feed's specific tile window is running.</summary>
    public bool IsRunning(string feedId, nint windowId)
    {
        if (windowId == 0) return false;
        if (!_sessions.TryGetValue(SessionKey(feedId, windowId), out var session)) return false;
        try { return !session.Process.HasExited; }
        catch { return false; }
    }

    /// <summary>Returns true while an mpv subprocess for this feed is running in any tile window.</summary>
    public bool IsRunning(string feedId)
    {
        var (_, session) = FindByFeedId(feedId);
        if (session == null) return false;
        try { return !session.Process.HasExited; }
        catch { return false; }
    }

    // Scans sessions for the first entry whose key starts with feedId.
    // O(n) over active tiles — acceptable given grids are small (≤9 tiles).
    private (string Key, ActiveSession? Session) FindByFeedId(string feedId)
    {
        var prefix = feedId + "\x00";
        foreach (var kv in _sessions)
        {
            if (kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return (kv.Key, kv.Value);
        }
        return (string.Empty, null);
    }

    /// <summary>Kills all active mpv processes.</summary>
    public void StopAll()
    {
        foreach (var key in _sessions.Keys.ToList())
            StopByKey(key);
    }

    private void StopByKey(string key)
    {
        if (_sessions.TryRemove(key, out var session))
        {
            try
            {
                if (!session.Process.HasExited)
                    session.Process.Kill();
            }
            catch { }
            finally
            {
                session.Process.Dispose();
                TryDeleteIpcSocket(session.IpcPath);
            }
        }
    }

    private static string BuildIpcPath()
    {
        return Path.Combine(Path.GetTempPath(), $"ccc-mpv-{Guid.NewGuid():N}.sock");
    }

    private static async Task LoadFileAsync(ActiveSession session, string url, string feedId)
    {
        try
        {
            using var span = TileTiming.Begin(feedId, "mpv.loadfile.ipc");
            await WaitForIpcSocketAsync(session.IpcPath, CancellationToken.None);
            await SendLoadFileCommandAsync(session.IpcPath, url, CancellationToken.None);
            Console.WriteLine($"[mpv] Sent loadfile for {feedId}: {url}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[mpv] loadfile failed for {feedId}: {ex.Message}");
        }
    }

    private static async Task<bool> SendLoadFileCommandAsync(string ipcPath, string url, CancellationToken ct)
    {
        const int requestId = 2;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(ipcPath), timeoutCts.Token);

        await using var stream = new NetworkStream(socket, ownsSocket: true);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 1024, leaveOpen: true);

        var payload = JsonSerializer.Serialize(new
        {
            command = new object[] { "loadfile", url, "replace" },
            request_id = requestId
        });

        await writer.WriteLineAsync(payload);

        while (!timeoutCts.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(timeoutCts.Token);
            if (string.IsNullOrWhiteSpace(line)) continue;

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("request_id", out var requestIdElement) || requestIdElement.GetInt32() != requestId)
                continue;

            return root.TryGetProperty("error", out var errorElement)
                && string.Equals(errorElement.GetString(), "success", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static async Task<bool> SendScreenshotCommandAsync(string ipcPath, string filePath, CancellationToken ct)
    {
        const int requestId = 1;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(ipcPath), timeoutCts.Token);

        await using var stream = new NetworkStream(socket, ownsSocket: true);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 1024, leaveOpen: true);

        var payload = JsonSerializer.Serialize(new
        {
            command = new object[] { "screenshot-to-file", filePath, "video" },
            request_id = requestId
        });

        await writer.WriteLineAsync(payload);

        while (!timeoutCts.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(timeoutCts.Token);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("request_id", out var requestIdElement) || requestIdElement.GetInt32() != requestId)
            {
                continue;
            }

            return root.TryGetProperty("error", out var errorElement)
                && string.Equals(errorElement.GetString(), "success", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static async Task WaitForIpcSocketAsync(string ipcPath, CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        while (!File.Exists(ipcPath))
        {
            if (DateTime.UtcNow - startedAt > TimeSpan.FromSeconds(2))
            {
                throw new TimeoutException("mpv IPC socket was not created in time.");
            }

            await Task.Delay(50, ct);
        }
    }

    private static void TryDeleteIpcSocket(string ipcPath)
    {
        try
        {
            if (File.Exists(ipcPath))
            {
                File.Delete(ipcPath);
            }
        }
        catch
        {
            // Ignore best-effort cleanup failures.
        }
    }
}
