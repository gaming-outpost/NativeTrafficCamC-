using System.Text.Json;
using CoastalCommandCenter.Application.Abstractions;
using CoastalCommandCenter.Models;

namespace CoastalCommandCenter.Infrastructure.Settings;

public sealed class JsonAppSettingsStore : IAppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public AppSettings Load()
    {
        var settingsPath = LocalStoragePaths.GetSettingsFilePath();
        if (!File.Exists(settingsPath))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(settingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[JsonAppSettingsStore] ERROR loading {settingsPath}: {ex.Message}");
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var settingsPath = LocalStoragePaths.GetSettingsFilePath();
        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(settingsPath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[JsonAppSettingsStore] ERROR writing {settingsPath}: {ex.Message}");
        }
    }
}
