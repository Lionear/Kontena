using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace Kontena.App.Converters;

/// <summary>
/// Expanded/collapsed to the matching chevron geometry — down when open, right when closed (KON-169).
/// <para>
/// A converter rather than two <c>Path</c>s toggled by <c>IsVisible</c>: two elements means two places
/// to keep the stroke, size and alignment in step, and the day they drift the chevron jumps as it turns.
/// </para>
/// </summary>
public sealed class BoolToChevronConverter : IValueConverter
{
    public static readonly BoolToChevronConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is true ? "IconChevronDown" : "IconChevronRight";

        return Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var resource)
            ? resource
            : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
