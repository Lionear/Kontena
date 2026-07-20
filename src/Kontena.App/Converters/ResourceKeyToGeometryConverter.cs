using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace Kontena.App.Converters;

/// <summary>Resolves a resource key (string) to the geometry stored under it.</summary>
public sealed class ResourceKeyToGeometryConverter : IValueConverter
{
    public static readonly ResourceKeyToGeometryConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string key
            && Application.Current is { } app
            && app.TryGetResource(key, app.ActualThemeVariant, out var resource))
        {
            return resource;
        }

        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
