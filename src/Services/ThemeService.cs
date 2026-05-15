using Avalonia;
using Avalonia.Media;
using CoastalCommandCenter.Models;

namespace CoastalCommandCenter.Services;

/// <summary>
/// Applies the Light (Ghidra/WinXP) or Dark (Eclipse/IDA Pro) theme palette
/// by mutating the Color of shared SolidColorBrush resources.
/// </summary>
public static class ThemeService
{
    public static AppThemeMode CurrentMode { get; private set; } = AppThemeMode.Light;

    // ── Light palette (Ghidra / WinXP Luna) ──────────────────────────────────
    private static readonly Dictionary<string, Color> LightColors = new()
    {
        ["BrushAppBg"]    = Color.Parse("#d4d0c8"),
        ["BrushPanelBg"]  = Color.Parse("#ece9d8"),
        ["BrushWindowBg"] = Color.Parse("#ffffff"),
        ["BrushTitleBar"] = Color.Parse("#0a246a"),
        ["BrushMenuBg"]   = Color.Parse("#d4d0c8"),

        ["BrushText"]      = Color.Parse("#000000"),
        ["BrushTextDim"]   = Color.Parse("#444444"),
        ["BrushTextTitle"] = Color.Parse("#ffffff"),

        ["BrushSelBg"]   = Color.Parse("#0a246a"),
        ["BrushSelText"] = Color.Parse("#ffffff"),

        ["BrushBorder"]  = Color.Parse("#808080"),
        ["BrushInputBg"] = Color.Parse("#ffffff"),
        ["BrushBtnBg"]   = Color.Parse("#d4d0c8"),

        ["BrushBevelHigh"] = Color.Parse("#ffffff"),
        ["BrushBevelShad"] = Color.Parse("#808080"),
        ["BrushFieldHigh"] = Color.Parse("#808080"),
        ["BrushFieldShad"] = Color.Parse("#dfdfdf"),

        ["BrushLiveDot"]  = Color.Parse("#00aa00"),
        ["BrushWarnDot"]  = Color.Parse("#cc6600"),
        ["BrushOffDot"]   = Color.Parse("#888888"),

        ["BrushEmbeddedGridBackground"]     = Color.Parse("#1e1e1e"),
        ["BrushPopoutWindowBackground"]     = Color.Parse("#1c1c2a"),
        ["BrushPopoutTitleBarBackground"]   = Color.Parse("#0f3460"),
        ["BrushPopoutTileHeaderBackground"] = Color.Parse("#0d2137"),
        ["BrushPopoutTitleForeground"]      = Color.Parse("#e0e0e0"),
        ["BrushPopoutCloseForeground"]      = Color.Parse("#e0e0e0"),
        ["BrushPopoutBorder"]               = Color.Parse("#2d6099"),
        ["BrushPopoutLabelText"]            = Color.Parse("#888888"),
        ["BrushPopoutOverlayBackground"]    = Color.Parse("#80000000"),
        ["BrushTextWhite"]                  = Color.Parse("#ffffff"),
        ["BrushTextSubtle"]                 = Color.Parse("#444444"),
        ["BrushAccentSecondary"]            = Color.Parse("#0a246a"),
        ["BrushDangerSecondary"]            = Color.Parse("#cc0000"),
    };

    // ── Dark palette (Eclipse Darcula / IDA Pro) ──────────────────────────────
    private static readonly Dictionary<string, Color> DarkColors = new()
    {
        ["BrushAppBg"]    = Color.Parse("#2d2d2d"),
        ["BrushPanelBg"]  = Color.Parse("#3c3f41"),
        ["BrushWindowBg"] = Color.Parse("#1e1e1e"),
        ["BrushTitleBar"] = Color.Parse("#1e4d80"),
        ["BrushMenuBg"]   = Color.Parse("#3c3f41"),

        ["BrushText"]      = Color.Parse("#bbbbbb"),
        ["BrushTextDim"]   = Color.Parse("#888888"),
        ["BrushTextTitle"] = Color.Parse("#ffffff"),

        ["BrushSelBg"]   = Color.Parse("#2d6099"),
        ["BrushSelText"] = Color.Parse("#ffffff"),

        ["BrushBorder"]  = Color.Parse("#555555"),
        ["BrushInputBg"] = Color.Parse("#2b2b2b"),
        ["BrushBtnBg"]   = Color.Parse("#3c3f41"),

        ["BrushBevelHigh"] = Color.Parse("#5a5a5a"),
        ["BrushBevelShad"] = Color.Parse("#1a1a1a"),
        ["BrushFieldHigh"] = Color.Parse("#1a1a1a"),
        ["BrushFieldShad"] = Color.Parse("#5a5a5a"),

        ["BrushLiveDot"]  = Color.Parse("#00cc44"),
        ["BrushWarnDot"]  = Color.Parse("#ffaa00"),
        ["BrushOffDot"]   = Color.Parse("#666666"),

        ["BrushEmbeddedGridBackground"]     = Color.Parse("#1e1e1e"),
        ["BrushPopoutWindowBackground"]     = Color.Parse("#1c1c2a"),
        ["BrushPopoutTitleBarBackground"]   = Color.Parse("#1e4d80"),
        ["BrushPopoutTileHeaderBackground"] = Color.Parse("#0d2845"),
        ["BrushPopoutTitleForeground"]      = Color.Parse("#e0e0e0"),
        ["BrushPopoutCloseForeground"]      = Color.Parse("#e0e0e0"),
        ["BrushPopoutBorder"]               = Color.Parse("#2d6099"),
        ["BrushPopoutLabelText"]            = Color.Parse("#888888"),
        ["BrushPopoutOverlayBackground"]    = Color.Parse("#80000000"),
        ["BrushTextWhite"]                  = Color.Parse("#ffffff"),
        ["BrushTextSubtle"]                 = Color.Parse("#888888"),
        ["BrushAccentSecondary"]            = Color.Parse("#2d6099"),
        ["BrushDangerSecondary"]            = Color.Parse("#ff4444"),
    };

    /// <summary>Applies the requested theme mode.</summary>
    public static void Apply(AppThemeMode mode)
    {
        CurrentMode = mode;
        var resources = Avalonia.Application.Current?.Resources;
        if (resources == null) return;

        var palette = mode == AppThemeMode.Dark ? DarkColors : LightColors;

        foreach (var (key, color) in palette)
        {
            if (resources.TryGetResource(key, null, out var res))
            {
                if (res is SolidColorBrush brush)
                    brush.Color = color;
            }
        }
    }

    public static SolidColorBrush GetBrush(string key)
    {
        var resources = Avalonia.Application.Current?.Resources
            ?? throw new InvalidOperationException("Application resources are unavailable.");

        if (resources.TryGetResource(key, null, out var resource))
        {
            if (resource is SolidColorBrush brush)
                return brush;
        }

        throw new KeyNotFoundException($"Brush resource '{key}' was not found.");
    }
}
