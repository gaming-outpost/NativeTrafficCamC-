using System.Diagnostics;
using System.Globalization;
using CoastalCommandCenter.Models;

namespace CoastalCommandCenter.Services;

public sealed class StreamCaptureService
{
    private static readonly string CaptureDebugLogPath =
        Path.Combine(Path.GetTempPath(), "ccc-capture.log");

    private readonly MpvPlayerService _mpvService;

    public StreamCaptureService(MpvPlayerService mpvService)
    {
        _mpvService = mpvService;
    }

    public async Task<CaptureOutcome> CaptureAllAsync(
        IReadOnlyList<VisibleStreamCaptureRequest> streams)
    {
        if (streams.Count == 0)
        {
            return new CaptureOutcome(0, 0, string.Empty);
        }

        var outputDirectory = ResolveCaptureOutputDirectory();
        Directory.CreateDirectory(outputDirectory);

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmssfff", CultureInfo.InvariantCulture);
        var fileNames = BuildCaptureFileMap(outputDirectory, streams, timestamp);

        var captureTasks = streams
            .Select(stream => CaptureStreamAsync(stream, fileNames[stream.StreamId]))
            .ToList();

        var results = await Task.WhenAll(captureTasks);
        var successCount = results.Count(r => r.Success);

        foreach (var failure in results.Where(r => !r.Success))
        {
            Console.WriteLine($"[capture] Failed for {failure.StreamName}: {failure.FilePath}");
            AppendDebugLog($"Capture failed for {failure.StreamName}: {failure.FilePath}");
        }

        AppendDebugLog(
            $"Capture complete. Success={successCount}/{streams.Count}. OutputDir={outputDirectory}");

        return new CaptureOutcome(successCount, streams.Count, outputDirectory);
    }

    private async Task<CaptureResult> CaptureStreamAsync(
        VisibleStreamCaptureRequest stream, string filePath)
    {
        try
        {
            var success = stream.Backend switch
            {
                StreamCaptureBackend.Mpv when !string.IsNullOrWhiteSpace(stream.MpvFeedId)
                    => await _mpvService.CaptureFrameAsync(stream.MpvFeedId, filePath),
                _ => false
            };

            if (!success && !string.IsNullOrWhiteSpace(stream.CaptureUrl))
            {
                success = await CaptureWithFfmpegAsync(stream.CaptureUrl, filePath);
            }

            AppendDebugLog(
                $"Capture stream {stream.CameraName} ({stream.Backend}) => {(success ? "ok" : "failed")} :: {filePath}");
            return new CaptureResult(stream.CameraName, filePath, success);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[capture] Exception for {stream.CameraName}: {ex.Message}");
            AppendDebugLog($"Capture exception for {stream.CameraName}: {ex}");
            return new CaptureResult(stream.CameraName, filePath, false);
        }
    }

    private static async Task<bool> CaptureWithFfmpegAsync(string sourceUrl, string filePath)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/ffmpeg",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("-y");
            startInfo.ArgumentList.Add("-loglevel");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(sourceUrl);
            startInfo.ArgumentList.Add("-frames:v");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("-update");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add(filePath);

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return false;
            }

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeoutCts.Token);
            return process.ExitCode == 0 && File.Exists(filePath);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[capture] ffmpeg fallback timed out.");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[capture] ffmpeg fallback failed: {ex.Message}");
            return false;
        }
    }

    internal static Dictionary<string, string> BuildCaptureFileMap(
        string outputDirectory,
        IReadOnlyList<VisibleStreamCaptureRequest> streams,
        string timestamp)
    {
        var fileNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var usedNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var stream in streams)
        {
            var region = SanitizeFileToken(stream.Region, uppercase: true);
            var cameraName = SanitizeFileToken(stream.CameraName, uppercase: false);
            var baseName = $"{region}_{cameraName}_{timestamp}";

            if (!usedNames.TryAdd(baseName, 0))
            {
                usedNames[baseName]++;
                baseName = $"{baseName}_{usedNames[baseName]}";
            }

            fileNames[stream.StreamId] = Path.Combine(outputDirectory, $"{baseName}.png");
        }

        return fileNames;
    }

    internal static string ResolveCaptureOutputDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory != null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CoastalCommandCenter.csproj")))
            {
                return Path.Combine(directory.FullName, "captures");
            }
        }

        return Path.Combine(AppContext.BaseDirectory, "captures");
    }

    internal static string SanitizeFileToken(string? value, bool uppercase)
    {
        var source = string.IsNullOrWhiteSpace(value) ? "UNKNOWN" : value.Trim();
        var buffer = new char[source.Length];
        var index = 0;
        var lastWasSeparator = false;

        foreach (var ch in source)
        {
            var normalized = char.IsLetterOrDigit(ch) ? ch : '_';
            if (normalized == '_')
            {
                if (lastWasSeparator)
                {
                    continue;
                }

                lastWasSeparator = true;
                buffer[index++] = '_';
                continue;
            }

            lastWasSeparator = false;
            buffer[index++] = uppercase ? char.ToUpperInvariant(normalized) : normalized;
        }

        var token = new string(buffer, 0, index).Trim('_');
        if (string.IsNullOrWhiteSpace(token))
        {
            return "UNKNOWN";
        }

        return uppercase ? token.ToUpperInvariant() : token;
    }

    [System.Diagnostics.Conditional("DEBUG")]
    internal static void AppendDebugLog(string message)
    {
        try
        {
            File.AppendAllText(
                CaptureDebugLogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // Ignore logging failures.
        }
    }

    public sealed record CaptureOutcome(int SuccessCount, int TotalCount, string OutputDirectory);

    private sealed record CaptureResult(string StreamName, string FilePath, bool Success);
}
