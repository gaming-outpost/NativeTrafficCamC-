using System.Runtime.InteropServices;

namespace CoastalCommandCenter.Infrastructure;

internal static class LocalStoragePaths
{
    public static string GetCameraDatabasePath()
    {
        var dataDir = GetApplicationDataDirectory();
        Directory.CreateDirectory(dataDir);
        return Path.Combine(dataDir, "cameras.sqlite");
    }

    public static string GetSettingsFilePath()
    {
        var configDir = GetApplicationConfigDirectory();
        Directory.CreateDirectory(configDir);
        return Path.Combine(configDir, "settings.json");
    }

    private static string GetApplicationDataDirectory()
    {
        string baseDir;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Library",
                "Application Support");
        }
        else
        {
            baseDir = Environment.GetEnvironmentVariable("XDG_DATA_HOME")
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local",
                    "share");
        }

        return Path.Combine(baseDir, "CoastalCommandCenter");
    }

    private static string GetApplicationConfigDirectory()
    {
        string baseDir;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }
        else
        {
            baseDir = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config");
        }

        return Path.Combine(baseDir, "CoastalCommandCenter");
    }
}
