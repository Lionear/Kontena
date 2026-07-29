using Avalonia.Media;

namespace Kontena.App.Controls;

/// <summary>
/// Picks the colour a brand mark is actually drawn in (KON-80).
/// <para>
/// A chip is the mark on a 16% wash of its own colour, which means a dark brand colour is dark-on-dark
/// and a light one is light-on-light: measured, Podman's violet sits on its own plate at 2.0:1 in the
/// dark theme and Docker's blue at 2.4:1 in the light one — both under the 3:1 floor WCAG sets for
/// graphics that carry meaning, and this one carries which engine you are looking at.
/// </para>
/// <para>
/// So the mark keeps the brand's hue and saturation and moves only its lightness, until it clears the
/// floor against its own plate. That is the same fix the palette makes by hand for <c>Accent</c>
/// (#7C3AED light, #A273F2 dark) — computed here because the colour arrives from a provider, and a
/// store-installed adapter cannot be asked to hand-tune a pair per theme.
/// </para>
/// </summary>
public static class BackendChipInk
{
    /// <summary>WCAG 1.4.11 — non-text contrast for a graphic that conveys information.</summary>
    public const double Floor = 3.0;

    /// <summary>How much of the accent the plate behind the mark is.</summary>
    public const double PlateOpacity = 0.16;

    /// <summary>
    /// The surface the mark has to clear. Not the surface it happens to sit on: a chip appears on the
    /// sidebar, in the popover, on a row and in a dialog, so it is measured against the least forgiving
    /// of them (Surface2 in both themes) and is then legible on all of them.
    /// </summary>
    private static readonly Color DarkReference = Color.Parse("#1C2136");
    private static readonly Color LightReference = Color.Parse("#F0F3F6");

    /// <summary>
    /// The mark's colour for a theme: the accent itself when it is already legible, otherwise the same
    /// hue lifted (dark) or deepened (light) until it clears <see cref="Floor"/>.
    /// </summary>
    public static Color For(string accent, bool dark)
    {
        var brand = Color.Parse(accent);
        var plate = Composite(brand, dark ? DarkReference : LightReference, PlateOpacity);

        if (Contrast(brand, plate) >= Floor)
            return brand;

        var (h, s, l) = ToHsl(brand);
        var step = dark ? 0.01 : -0.01;

        // Walk towards white or black one point at a time and stop at the first colour that clears —
        // the closest legible version of the brand colour rather than a safe grey.
        for (var i = 1; i <= 97; i++)
        {
            var moved = Math.Clamp(l + step * i, 0.03, 0.97);
            var candidate = FromHsl(h, s, moved);
            if (Contrast(candidate, plate) >= Floor)
                return candidate;

            if (moved is <= 0.03 or >= 0.97)
                break;
        }

        // Nothing on this hue clears its own plate — fall back to the extreme rather than to the
        // unreadable original.
        return FromHsl(h, s, dark ? 0.97 : 0.03);
    }

    /// <summary>Flatten <paramref name="over"/> at <paramref name="opacity"/> onto <paramref name="onto"/>.</summary>
    public static Color Composite(Color over, Color onto, double opacity) => Color.FromRgb(
        (byte)Math.Round(over.R * opacity + onto.R * (1 - opacity)),
        (byte)Math.Round(over.G * opacity + onto.G * (1 - opacity)),
        (byte)Math.Round(over.B * opacity + onto.B * (1 - opacity)));

    /// <summary>WCAG relative-luminance contrast ratio.</summary>
    public static double Contrast(Color a, Color b)
    {
        var (high, low) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (high + 0.05) / (low + 0.05);
    }

    private static double Luminance(Color c)
        => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    private static double Channel(byte v)
    {
        var c = v / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;

        if (max - min < 1e-9)
            return (0, 0, l);

        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        var h = max == r ? (g - b) / d + (g < b ? 6 : 0)
            : max == g ? (b - r) / d + 2
            : (r - g) / d + 4;

        return (h / 6, s, l);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        if (s < 1e-9)
        {
            var v = (byte)Math.Round(l * 255);
            return Color.FromRgb(v, v, v);
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;

        return Color.FromRgb(Component(p, q, h + 1.0 / 3), Component(p, q, h), Component(p, q, h - 1.0 / 3));
    }

    private static byte Component(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;

        var v = t < 1.0 / 6 ? p + (q - p) * 6 * t
            : t < 1.0 / 2 ? q
            : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6
            : p;

        return (byte)Math.Round(v * 255);
    }
}
