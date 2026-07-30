using System.Text.RegularExpressions;
using Kontena.App.Controls;

namespace Kontena.App.Tests;

/// <summary>
/// The surface a backend chip is measured against is the surface that exists (KON-245).
/// <para>
/// <see cref="BackendChipInk"/> decides whether a vendor's brand colour is legible by compositing it
/// over the least forgiving surface a chip lands on, <c>Surface2</c>. That value is a literal in the
/// control, because the calculation runs without a theme to ask — and a literal copy of a palette token
/// is a copy that goes stale the first time the palette moves. Which is exactly what happened: the dark
/// ladder was desaturated and the reference still named the old navy.
/// </para>
/// <para>
/// The consequence is quiet, which is why this is a test rather than a comment: nothing throws, the
/// chip is simply judged against a background it is no longer drawn on, and a mark that should have
/// been lifted stays as it was.
/// </para>
/// </summary>
public sealed class ChipReferenceSurfaceTests
{
    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void The_reference_surface_matches_the_palette(string theme)
    {
        var expected = Surface2Of(theme);
        var actual = BackendChipInk.ReferenceSurface(dark: theme == "Dark").ToString();

        Assert.Equal(expected, actual, ignoreCase: true);
    }

    /// <summary>Surface2 as <c>App.axaml</c> defines it — the same source PaletteContrastTests reads.</summary>
    private static string Surface2Of(string theme)
    {
        var xaml = File.ReadAllText(AppXamlPath());

        var dictionary = Regex.Match(
            xaml, $@"<ResourceDictionary x:Key=""{theme}"">(.*?)</ResourceDictionary>", RegexOptions.Singleline);
        Assert.True(dictionary.Success, $"No {theme} dictionary in App.axaml.");

        var brush = Regex.Match(dictionary.Groups[1].Value, @"x:Key=""Surface2""\s+Color=""(#[0-9A-Fa-f]{6})""");
        Assert.True(brush.Success, $"No Surface2 in the {theme} dictionary.");

        return "#FF" + brush.Groups[1].Value[1..];
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
}
