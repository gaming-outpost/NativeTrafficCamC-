using System.Globalization;
using Avalonia.Data.Converters;

namespace CoastalCommandCenter.ViewModels;

/// <summary>
/// Converts a boolean <c>IsExpanded</c> value to a chevron character for the group header.
/// </summary>
public sealed class BoolToChevronConverter : IValueConverter
{
    /// <summary>Shared singleton instance for use in XAML.</summary>
    public static readonly BoolToChevronConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "▾" : "▸";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
