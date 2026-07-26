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
/// The point is not that everything passes today. Two tokens do not, and fixing them is a design
/// decision rather than a test's business. What this pins down is that the list cannot grow quietly.
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

    /// <summary>Surfaces text sits on.</summary>
    private static readonly string[] Backgrounds =
        ["Bg", "Surface", "Surface2", "SidebarBg", "SurfaceRaised"];

    /// <summary>
    /// Known to fall short of AA as body text, with the reason. Anything not on this list must pass —
    /// that is what stops the list from growing by accident.
    /// </summary>
    private static readonly Dictionary<string, string> BelowAa = new(StringComparer.Ordinal)
    {
        // Used as body text in 159 places and reaching only 2.7:1 — the single biggest accessibility
        // defect in the app. Correcting it shifts a colour across every screen, so it is Rick's call,
        // not a test's. Measured proposals live in the KON-56 ticket.
        ["Dark/TextFaint"] = "2.71:1 on Surface2 — proposed #808B9B reaches 4.57:1",
        ["Light/TextFaint"] = "2.76:1 on Surface2 — proposed #65707F reaches 4.51:1",

        // Status colours carry short, semibold labels (chips, driver names, node conditions) rather
        // than running text. They clear the large-text floor; raising them to 4.5 would flatten the
        // palette's distinctions.
        ["Light/Primary"] = "status label, 3.04:1 — above the 3:1 large-text floor",
        ["Light/Success"] = "status label, 3.04:1 — above the 3:1 large-text floor",
        ["Light/Info"] = "status label, 3.60:1 — above the 3:1 large-text floor",
        ["Light/Danger"] = "status label, 3.51:1 — above the 3:1 large-text floor",
        // The one status colour that clears no threshold at all — 2.80:1 on Surface2, 3.11:1 at its
        // best. Unlike the others this is not a judgement call about label size; it is simply too
        // pale. Proposed #956314 reaches 4.63:1.
        ["Light/Warn"] = "2.80:1 — clears neither AA nor the 3:1 large-text floor",
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

                // TextFaint and light's Warn are the two open questions, each pinned at where it is
                // now so it cannot drift further while the decision is pending. Everything else on the
                // list must at least clear the large-text floor.
                var floor = fg is "TextFaint" ? 2.5 : fg == "Warn" ? 2.75 : AALarge;
                if (worst < floor)
                    sunk.Add($"{theme}: {fg} ({palette[fg]}) worst case {worst:0.00}:1, floor {floor:0.0}:1");
            }
        }

        Assert.True(sunk.Count == 0, string.Join(Environment.NewLine, sunk));
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
