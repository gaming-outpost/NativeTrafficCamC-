using System.Collections.Concurrent;
using System.Diagnostics;

namespace CoastalCommandCenter.Services;

/// <summary>
/// Shells out to yt-dlp to extract direct stream URLs from YouTube live streams.
/// </summary>
public class YtDlpService
{
    private readonly ConcurrentDictionary<string, CachedUrl> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    private static readonly Lazy<string?> ResolvedExecutable = new(ResolveExecutablePath);
    private const string CompatibilityFormatSelector =
        "best[protocol=m3u8][vcodec^=avc1][acodec^=mp4a][height<=480]"
        + "/best[ext=mp4][protocol=https][vcodec^=avc1][acodec^=mp4a][height<=480]"
        + "/best[protocol=m3u8][height<=480]"
        + "/best[height<=480]";
    private static readonly string[] CommonExtractArgs =
    [
        "--no-warnings",
        "--no-playlist",
        "--no-check-certificates",
        "--socket-timeout", "15",
        "--cookies-from-browser", "brave",
        "--extractor-args", "youtube:player_client=web"
    ];

    private record CachedUrl(string DirectUrl, DateTime ExpiresAtUtc);
    private sealed record UrlSelectionResult(string? Url, string SelectionType, int UrlLineCount, int RawLineCount);

    public sealed record YtDlpResolveResult(string? DirectUrl, string? Error)
    {
        public bool Success => !string.IsNullOrWhiteSpace(DirectUrl);

        public static YtDlpResolveResult FromUrl(string directUrl) => new(directUrl, null);
        public static YtDlpResolveResult Failed(string error) => new(null, error);
    }

    public async Task<YtDlpResolveResult> ResolveStreamUrlAsync(string youtubeUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(youtubeUrl))
            return YtDlpResolveResult.Failed("YouTube URL is empty.");

        if (!IsAllowedStreamUrl(youtubeUrl))
            return YtDlpResolveResult.Failed("URL is not a valid HTTP/HTTPS stream URL.");

        var normalizedUrl = NormalizeYoutubeUrl(youtubeUrl);

        if (_cache.TryGetValue(normalizedUrl, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
        {
            Console.WriteLine($"[yt-dlp] Cache hit for {normalizedUrl}");
            return YtDlpResolveResult.FromUrl(cached.DirectUrl);
        }

        // Compatibility-first extraction for YouTube feeds.
        var result = await RunExtractCommandAsync(
            normalizedUrl,
            BuildExtractArgs(CompatibilityFormatSelector),
            ct);

        if (result.Success)
        {
            _cache[normalizedUrl] = new CachedUrl(result.DirectUrl!, DateTime.UtcNow + CacheTtl);
            return result;
        }

        Console.WriteLine($"[yt-dlp] First attempt failed ({result.Error}), retrying with 'best'");

        // Last-resort: no format filter at all.
        var fallback = await RunExtractCommandAsync(
            normalizedUrl,
            BuildExtractArgs("best"),
            ct);

        if (fallback.Success)
        {
            _cache[normalizedUrl] = new CachedUrl(fallback.DirectUrl!, DateTime.UtcNow + CacheTtl);
            return fallback;
        }

        var error = string.Join(" | ",
            new[] { result.Error, fallback.Error }.Where(m => !string.IsNullOrWhiteSpace(m)));

        return YtDlpResolveResult.Failed(
            string.IsNullOrWhiteSpace(error)
                ? "yt-dlp could not resolve a playable stream URL."
                : $"yt-dlp could not resolve a playable stream URL: {error}");
    }

    private static async Task<YtDlpResolveResult> RunExtractCommandAsync(
        string normalizedUrl,
        string[] args,
        CancellationToken ct)
    {
        var executable = ResolvedExecutable.Value;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return YtDlpResolveResult.Failed(
                "yt-dlp executable not found. Set YTDLP_PATH or install yt-dlp in PATH.");
        }

        var fullCommand = $"{executable} {string.Join(' ', args)} {normalizedUrl}";
        Console.WriteLine($"[yt-dlp] Running: {fullCommand}");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // Propagate PATH and HOME so yt-dlp finds its own dependencies
            // (ffmpeg, cookies, config files) the same way the user's shell does.
            psi.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/local/bin:/usr/bin:/bin";
            psi.Environment["HOME"] = Environment.GetEnvironmentVariable("HOME") ?? "/root";

            // Launch through /bin/sh to avoid .NET Process.Start issues with
            // Python scripts on Linux (TTY detection, pipe buffering, etc.).
            // exec "$0" "$@": $0 = executable, $@ = remaining args.
            // exec replaces the shell with yt-dlp (no extra process overhead).
            // ArgumentList ensures each arg is properly passed as a separate
            // positional parameter, preventing shell injection.
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("exec \"$0\" \"$@\"");
            psi.ArgumentList.Add(executable);  // becomes $0
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);     // become $1, $2, ...
            psi.ArgumentList.Add(normalizedUrl);

            var sw = Stopwatch.StartNew();
            using var process = Process.Start(psi);
            if (process == null)
                return YtDlpResolveResult.Failed("Failed to start yt-dlp process.");

            // Read stdout and stderr concurrently with WaitForExitAsync.
            // Reading them after WaitForExit risks a deadlock: the process
            // blocks on a full pipe buffer while we're waiting for it to exit.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(45));

            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errorTask  = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await Task.WhenAll(
                    process.WaitForExitAsync(timeoutCts.Token),
                    outputTask,
                    errorTask);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch { /* best-effort */ }
                return YtDlpResolveResult.Failed(
                    ct.IsCancellationRequested
                        ? "yt-dlp request cancelled."
                        : "yt-dlp timed out after 45 seconds.");
            }

            sw.Stop();
            var output = outputTask.Result;
            var error  = errorTask.Result.Trim();

            Console.WriteLine($"[yt-dlp] Exit {process.ExitCode} in {sw.ElapsedMilliseconds}ms");
            if (!string.IsNullOrWhiteSpace(error))
                Console.WriteLine($"[yt-dlp] stderr: {error}");

            if (process.ExitCode != 0)
            {
                return YtDlpResolveResult.Failed(
                    string.IsNullOrWhiteSpace(error)
                        ? $"yt-dlp failed with exit code {process.ExitCode}."
                        : $"yt-dlp failed: {error}");
            }

            var selection = SelectBestResolvedUrl(output);
            Console.WriteLine(
                $"[yt-dlp] URL lines raw={selection.RawLineCount}, valid={selection.UrlLineCount}, selected={selection.SelectionType}");

            if (string.IsNullOrWhiteSpace(selection.Url))
            {
                return selection.RawLineCount == 0
                    ? YtDlpResolveResult.Failed("yt-dlp returned no stream URL lines.")
                    : YtDlpResolveResult.Failed(
                        $"yt-dlp returned {selection.RawLineCount} line(s), but none were usable stream URLs.");
            }

            Console.WriteLine($"[yt-dlp] Resolved ({selection.SelectionType}): {selection.Url[..Math.Min(80, selection.Url.Length)]}…");
            return YtDlpResolveResult.FromUrl(selection.Url);
        }
        catch (OperationCanceledException)
        {
            return YtDlpResolveResult.Failed("yt-dlp request cancelled.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[yt-dlp] Exception: {ex.Message}");
            return YtDlpResolveResult.Failed(ex.Message);
        }
    }

    private static string[] BuildExtractArgs(string formatSelector)
    {
        var args = new List<string>(CommonExtractArgs.Length + 3)
        {
            "-f",
            formatSelector,
            "-g"
        };
        args.AddRange(CommonExtractArgs);
        return [.. args];
    }

    private static UrlSelectionResult SelectBestResolvedUrl(string output)
    {
        var rawLines = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        var urlLines = rawLines
            .Where(IsAllowedStreamUrl)
            .ToList();

        if (urlLines.Count == 0)
        {
            return new UrlSelectionResult(null, "none", 0, rawLines.Count);
        }

        var hlsUrl = urlLines.FirstOrDefault(url =>
            IsLikelyHlsUrl(url) && !IsLikelyAudioOnlyUrl(url));
        if (!string.IsNullOrWhiteSpace(hlsUrl))
        {
            return new UrlSelectionResult(hlsUrl, "hls", urlLines.Count, rawLines.Count);
        }

        var directUrl = urlLines.FirstOrDefault(url => !IsLikelyAudioOnlyUrl(url));
        if (!string.IsNullOrWhiteSpace(directUrl))
        {
            return new UrlSelectionResult(directUrl, "direct", urlLines.Count, rawLines.Count);
        }

        return new UrlSelectionResult(urlLines[0], "fallback", urlLines.Count, rawLines.Count);
    }

    private static bool IsLikelyHlsUrl(string url)
    {
        return url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
            || url.Contains("manifest/hls_playlist", StringComparison.OrdinalIgnoreCase)
            || url.Contains("/hls/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLikelyAudioOnlyUrl(string url)
    {
        return url.Contains("mime=audio", StringComparison.OrdinalIgnoreCase)
            || url.Contains("mime%3Daudio", StringComparison.OrdinalIgnoreCase)
            || url.Contains("mime%253Daudio", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeYoutubeUrl(string url)
    {
        if (url.Contains("youtube-nocookie.com/embed/", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("youtube.com/embed/", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(url);
            // Last path segment is the video ID; strip query params and trailing slashes.
            var videoId = uri.Segments.LastOrDefault()?.TrimEnd('/');
            if (!string.IsNullOrEmpty(videoId) && videoId != "embed")
                return $"https://www.youtube.com/watch?v={videoId}";
        }

        return url;
    }

    public static bool IsAvailable()
    {
        return !string.IsNullOrWhiteSpace(ResolvedExecutable.Value);
    }

    public void PurgeExpiredCache()
    {
        var now = DateTime.UtcNow;
        foreach (var key in _cache.Keys.ToList())
        {
            if (_cache.TryGetValue(key, out var entry) && entry.ExpiresAtUtc < now)
                _cache.TryRemove(key, out _);
        }
    }

    private static string? ResolveExecutablePath()
    {
        var envOverride = Environment.GetEnvironmentVariable("YTDLP_PATH");
        if (!string.IsNullOrWhiteSpace(envOverride) && CanExecute(envOverride))
        {
            Console.WriteLine($"[yt-dlp] Resolved via YTDLP_PATH: {envOverride}");
            return envOverride;
        }

        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            "yt-dlp",
            "/usr/bin/yt-dlp",
            "/usr/local/bin/yt-dlp",
            Path.Combine(baseDir, "yt-dlp"),
            Path.Combine(baseDir, "tools", "yt-dlp")
        };

        foreach (var candidate in candidates)
        {
            if (CanExecute(candidate))
            {
                Console.WriteLine($"[yt-dlp] Resolved: {candidate}");
                return candidate;
            }
        }

        // Last resort: ask the shell where yt-dlp lives.
        var whichPath = RunWhich("yt-dlp");
        if (!string.IsNullOrWhiteSpace(whichPath) && CanExecute(whichPath))
        {
            Console.WriteLine($"[yt-dlp] Resolved via 'which': {whichPath}");
            return whichPath;
        }

        Console.WriteLine("[yt-dlp] ERROR: executable not found in any candidate location.");
        return null;
    }

    private static string? RunWhich(string name)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add($"which {name}");

            using var process = Process.Start(psi);
            var output = process?.StandardOutput.ReadToEnd().Trim();
            process?.WaitForExit(3000);
            return string.IsNullOrWhiteSpace(output) ? null : output;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Validates that a URL uses an allowed scheme (http/https) to prevent
    /// command injection or unexpected protocol access.
    /// </summary>
    internal static bool IsAllowedStreamUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        return uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanExecute(string executable)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            process?.WaitForExit(3000);
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
