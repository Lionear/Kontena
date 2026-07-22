using System.Globalization;
using Avalonia.Data.Converters;

namespace Kontena.App.Converters;

/// <summary>
/// A fraction of a measured length, for panes that should take a share of what they are given
/// rather than as much as their content wants. Star rows would claim their share even when empty;
/// this feeds a <c>MaxHeight</c>, so a short list still shrinks to fit.
/// </summary>
public sealed class FractionConverter : IValueConverter
{
    public static readonly FractionConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double length || double.IsNaN(length) || length <= 0)
            return double.PositiveInfinity;

        var fraction = parameter switch
        {
            double d => d,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 1d,
        };

        return length * fraction;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
