using System.Globalization;
using System.Text.RegularExpressions;

namespace Kontena.App.Tests;

/// <summary>
/// Contrast of the theme palettes, measured rather than eyeballed (KON-56).
/// <para>
/// Reads the brushes straight out of <c>App.axaml</c>, so it measures what ships. A colour has already
/// slipped through here once — <c>DangerSoft</c> written as <c>#F8717120</c> parsed as opaque olive,
/// which nobody spotted until a screenshot showed olive prune buttons.
/// </para>
/// <para>
/// Every text colour clears AA. The four that do not are named, with the reason: they carry short
/// semibold status labels rather than running text, and clear the large-text floor. That list can
/// shrink; what this pins down is that it cannot grow quietly.
/// </para>
/// </summary>
public sealed class PaletteContrastTests
{
    /// <summary>WCAG AA for body text.</summary>
    private const double AA = 4.5;

    /// <summary>WCAG AA for large text and graphical objects — the floor even for the exceptions.</summary>
    private const double AALarge = 3.0;

    /// <summary>Tokens used as a text foreground somewhere in the views.</summary>
    private static readonly string[] Foregrounds =
        ["Text", "TextDim", "TextFaint", "Primary", "Success", "Info", "Warn", "Danger", "WarnText"];

    /// <summary>
    /// Surfaces text sits on.
    /// <para>
    /// <c>CodeSurface</c> is here because it follows the theme like any other panel (KON-196).
    /// <c>Console</c> deliberately is not: it stays dark in both themes, so the palette's text tokens
    /// are the wrong measure against it — in light they are dark too, and light's <c>TextDim</c> sits on
    /// it at 2.89:1. That is a real gap and it has its own ticket; adding the surface here without the
    /// console's own ink tokens would only report a failure this test cannot fix.
    /// <para>
    /// The tinted washes (<c>DangerSoft</c> and friends) are deliberately absent, and that is a known
    /// gap rather than an oversight: in dark they are translucent, so a flat ratio would be a guess,
    /// and covering them in light only would report half a picture. Measured by hand meanwhile —
    /// <c>WarnText</c> on <c>WarnSoft</c> is fine at 4.82:1, but the prune buttons (<c>Danger</c> on
    /// <c>DangerSoft</c>, 3.30:1) and the two info blocks sit below AA. Noted on KON-56; fixing it
    /// needs the blend computed first.
    /// </para>
    /// </summary>
    private static readonly string[] Backgrounds =
        ["Bg", "Surface", "Surface2", "SidebarBg", "SurfaceRaised", "CodeSurface"];

    /// <summary>
    /// Known to fall short of AA as body text, with the reason. Anything not on this list must pass —
    /// that is what stops the list from growing by accident.
    /// </summary>
    private static readonly Dictionary<string, string> BelowAa = new(StringComparer.Ordinal)
    {
        // Status colours carry short, semibold labels (chips, driver names, node conditions) rather
        // than running text. They clear the large-text floor; raising them to 4.5 would flatten the
        // palette's distinctions, which is a design call rather than a defect.
        //
        // TextFaint in both themes and Warn in light used to be on this list too. They were not
        // judgement calls — TextFaint carried body text at 2.7:1 in 159 places, and light's Warn
        // cleared no threshold at all — so they were corrected instead of excused (KON-56).
        //
        // Light/Primary left the same way under KON-130. Calling it a status label was only ever half
        // true: it also filled the primary button, where the white PrimaryInk label sat on it at
        // 3.38:1. #007A5E clears AA everywhere, so the entry is gone rather than reworded.
        // Light/Danger went the same way, and for the same reason: it filled the destructive button
        // under a white label at 3.91:1. Deepening it to #D61E24 cleared both roles at once.
        ["Light/Success"] = "status label, 3.04:1 — above the 3:1 large-text floor",
        ["Light/Info"] = "status label, 3.60:1 — above the 3:1 large-text floor",
    };

    [Fact]
    public void Every_text_colour_not_on_the_known_list_meets_AA()
    {
        var failures = new List<string>();

        foreach (var (theme, palette) in Palettes())
        {
            foreach (var fg in Foregrounds.Where(palette.ContainsKey))
            {
                if (BelowAa.ContainsKey($"{theme}/{fg}"))
                    continue;

                foreach (var bg in Backgrounds.Where(palette.ContainsKey))
                {
                    var ratio = Contrast(palette[fg], palette[bg]);
                    if (ratio < AA)
                        failures.Add($"{theme}: {fg} ({palette[fg]}) on {bg} ({palette[bg]}) = {ratio:0.00}:1");
                }
            }
        }

        Assert.True(failures.Count == 0,
            "New colours fell below AA:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void The_known_exceptions_do_not_sink_further()
    {
        // A token allowed to miss AA still has a floor. Without this the exception list would be a
        // licence for the colour to drift anywhere.
        var sunk = new List<string>();

        foreach (var (theme, palette) in Palettes())
        {
            foreach (var fg in Foregrounds.Where(palette.ContainsKey))
            {
                if (!BelowAa.ContainsKey($"{theme}/{fg}"))
                    continue;

                // Against the surface it is most likely to sit on — the one it does worst against.
                var worst = Backgrounds.Where(palette.ContainsKey).Min(bg => Contrast(palette[fg], palette[bg]));

                if (worst < AALarge)
                    sunk.Add($"{theme}: {fg} ({palette[fg]}) worst case {worst:0.00}:1, floor {AALarge:0.0}:1");
            }
        }

        Assert.True(sunk.Count == 0, string.Join(Environment.NewLine, sunk));
    }

    /// <summary>
    /// Ink tokens paired with the fill they sit on. Filled buttons are not covered by the surface
    /// sweep above, which is how light's white-on-#0F9E7E label sat at 3.38:1 while the palette was
    /// nominally clean — Primary was excused as a status colour, and on the button it was not one.
    /// </summary>
    private static readonly (string Ink, string Fill)[] InkOnFill =
        [("PrimaryInk", "Primary"), ("DangerInk", "Danger")];

    [Fact]
    public void Label_ink_meets_AA_against_the_fill_it_sits_on()
    {
        var failures = new List<string>();

        foreach (var (theme, palette) in Palettes())
        {
            foreach (var (ink, fill) in InkOnFill.Where(p => palette.ContainsKey(p.Ink) && palette.ContainsKey(p.Fill)))
            {
                var ratio = Contrast(palette[ink], palette[fill]);
                if (ratio < AA)
                    failures.Add($"{theme}: {ink} ({palette[ink]}) on {fill} ({palette[fill]}) = {ratio:0.00}:1");
            }
        }

        Assert.True(failures.Count == 0,
            "Button labels below AA against their own fill:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Every_exception_says_why_it_is_one()
    {
        // A bare list of tokens rots into "someone must have had a reason". Each entry carries it.
        Assert.All(BelowAa.Values, reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
    }

    [Fact]
    public void Both_themes_define_the_same_tokens()
    {
        // A token that exists in one theme and not the other renders as a missing resource — which
        // Avalonia reports at runtime, not at build.
        //
        // Compares the names in the file, not the opaque subset the contrast tests use: the soft
        // washes are translucent in dark and pre-blended in light, so filtering by opacity first
        // would report three tokens as missing that are defined in both.
        var names = TokenNames().ToDictionary(p => p.Theme, p => p.Names);

        var onlyDark = names["Dark"].Except(names["Light"]).ToList();
        var onlyLight = names["Light"].Except(names["Dark"]).ToList();

        Assert.True(onlyDark.Count == 0, "Only in Dark: " + string.Join(", ", onlyDark));
        Assert.True(onlyLight.Count == 0, "Only in Light: " + string.Join(", ", onlyLight));
    }

    // ── Reading the palette out of the app's own XAML ────────────────────────

    private static IEnumerable<(string Theme, Dictionary<string, string> Palette)> Palettes()
    {
        var xaml = File.ReadAllText(AppXamlPath());

        foreach (Match theme in Regex.Matches(
                     xaml, @"<ResourceDictionary x:Key=""(Dark|Light)"">(.*?)</ResourceDictionary>",
                     RegexOptions.Singleline))
        {
            var palette = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (Match brush in Regex.Matches(
                         theme.Groups[2].Value,
                         @"x:Key=""(\w+)""\s+Color=""(#[0-9A-Fa-f]{6})""(?<translucent>\s+Opacity=""[\d.]+"")?"))
            {
                // Translucent brushes blend with whatever is behind them, so a flat ratio would be a
                // guess. They are backgrounds and washes, never text.
                if (brush.Groups["translucent"].Success)
                    continue;

                palette[brush.Groups[1].Value] = brush.Groups[2].Value;
            }

            yield return (theme.Groups[1].Value, palette);
        }
    }

    private static IEnumerable<(string Theme, IReadOnlyCollection<string> Names)> TokenNames()
    {
        var xaml = File.ReadAllText(AppXamlPath());

        foreach (Match theme in Regex.Matches(
                     xaml, @"<ResourceDictionary x:Key=""(Dark|Light)"">(.*?)</ResourceDictionary>",
                     RegexOptions.Singleline))
        {
            yield return (
                theme.Groups[1].Value,
                Regex.Matches(theme.Groups[2].Value, @"x:Key=""(\w+)""")
                    .Select(m => m.Groups[1].Value)
                    .ToHashSet(StringComparer.Ordinal));
        }
    }

    private static string AppXamlPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Kontena.App", "App.axaml");
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not find src/Kontena.App/App.axaml above the test binary.");
    }

    // ── WCAG relative luminance ──────────────────────────────────────────────

    private static double Contrast(string foreground, string background)
    {
        var a = Luminance(foreground);
        var b = Luminance(background);
        var (hi, lo) = a > b ? (a, b) : (b, a);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double Luminance(string hex)
    {
        var r = Channel(hex, 1);
        var g = Channel(hex, 3);
        var b = Channel(hex, 5);
        return (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
    }

    private static double Channel(string hex, int offset)
    {
        var value = int.Parse(hex.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
        return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }
}
