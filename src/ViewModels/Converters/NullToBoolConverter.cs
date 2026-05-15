using System.Globalization;
using Avalonia.Data.Converters;

namespace CoastalCommandCenter.ViewModels;

/// <summary>
/// Converts a nullable object to a boolean.
/// <see cref="IsNull"/> returns true when the value is null.
/// <see cref="IsNotNull"/> returns true when the value is not null.
/// </summary>
public sealed class NullToBoolConverter : IValueConverter
{
    public static readonly NullToBoolConverter IsNull    = new(returnTrueWhenNull: true);
    public static readonly NullToBoolConverter IsNotNull = new(returnTrueWhenNull: false);

    private readonly bool _returnTrueWhenNull;

    private NullToBoolConverter(bool returnTrueWhenNull)
        => _returnTrueWhenNull = returnTrueWhenNull;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => _returnTrueWhenNull ? value is null : value is not null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
