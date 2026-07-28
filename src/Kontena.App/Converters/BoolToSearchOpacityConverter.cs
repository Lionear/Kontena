using System.Globalization;
using Avalonia.Data.Converters;

namespace Kontena.App.Converters;

/// <summary>
/// Full opacity when the search box is usable, dimmed when it is not (KON-164).
/// <para>
/// The box is disabled rather than hidden: a command bar that loses a control as you move between
/// pages is more distracting than one where the control is visibly unavailable, and hiding it would
/// shift everything beside it.
/// </para>
/// </summary>
public sealed class BoolToSearchOpacityConverter : IValueConverter
{
    public static readonly BoolToSearchOpacityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? 1.0 : 0.45;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
