using Avalonia.Media.Imaging;
using CoastalCommandCenter.Models;

namespace CoastalCommandCenter.Services;

public sealed record StaticSnapshotFetchResult(Bitmap Bitmap, DateTimeOffset RetrievedAt);

public sealed class StaticSnapshotService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public StaticSnapshotService()
        : this(new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        }, ownsHttpClient: true)
    {
    }

    public StaticSnapshotService(HttpClient httpClient, bool ownsHttpClient = false)
    {
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
    }

    public async Task<StaticSnapshotFetchResult> LoadSnapshotAsync(
        StaticCameraDefinition camera,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(camera);

        var requestUrl = BuildCacheBustedUrl(
            camera.BaseSnapshotUrl,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        using var response = await _httpClient.GetAsync(
            requestUrl,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(ct);
        using var memoryStream = new MemoryStream();
        await responseStream.CopyToAsync(memoryStream, ct);
        memoryStream.Position = 0;

        var bitmap = new Bitmap(memoryStream);
        return new StaticSnapshotFetchResult(bitmap, DateTimeOffset.Now);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static string BuildCacheBustedUrl(string baseUrl, long utcUnixMilliseconds)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("Snapshot URL is empty.");
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var absoluteUri))
        {
            throw new InvalidOperationException($"Invalid snapshot URL: {baseUrl}");
        }

        if (!absoluteUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            && !absoluteUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unsupported snapshot URL scheme: {absoluteUri.Scheme}");
        }

        var queryParts = absoluteUri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !part.StartsWith("arg=", StringComparison.OrdinalIgnoreCase))
            .ToList();
        queryParts.Add($"arg={utcUnixMilliseconds}");

        var uriBuilder = new UriBuilder(absoluteUri)
        {
            Query = string.Join("&", queryParts)
        };

        return uriBuilder.Uri.ToString();
    }
}
