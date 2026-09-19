using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace AppPortal.Client.Services;

/// <summary>Shows server timestamps (UTC) in the user's local time.</summary>
public sealed class LocalTimeConverter : IValueConverter
{
    public static readonly LocalTimeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            DateTimeOffset dto => dto.ToLocalTime().ToString("g", culture),
            DateTime dt => dt.ToLocalTime().ToString("g", culture),
            _ => "",
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
