using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace Kontena.App.Converters;

/// <summary>Resolves a resource key (string) to the brush stored under it, theme-aware.</summary>
public sealed class ResourceKeyToBrushConverter : IValueConverter
{
    public static readonly ResourceKeyToBrushConverter Instance = new();

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
