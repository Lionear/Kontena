using System.Text.RegularExpressions;

namespace Kontena.App.Tests;

/// <summary>
/// The corner radius comes from the scale, or it is one of the shapes that is not on it (KON-220).
/// <para>
/// Before this, every corner was a literal and they had drifted to nine different values — 7 here, 8
/// there, 10, 12, 15, 20. Nothing was wrong with any one of them, which is exactly why it spread: a
/// new view copies whatever the view next to it used. This makes the next literal a failing build
/// rather than a tenth value.
/// </para>
/// </summary>
public sealed class RadiusScaleTests
{
    /// <summary>
    /// Radii that are deliberately off the scale, with the reason they are. Kept small on purpose —
    /// like the palette's exception list, this may shrink but should not grow.
    /// </summary>
    private static readonly Dictionary<string, string> OffScale = new(StringComparer.Ordinal)
    {
        ["0"] = "deliberately square",
        ["2"] = "a 4px progress bar",
        ["3"] = "badges a few pixels tall, where 6 reads as a lozenge",
        ["4"] = "same",
        ["5"] = "same",
        ["16"] = "64px empty-state tile — radius tracks the box",
        ["18"] = "72px empty-state tile — same",
        ["20"] = "capsule",
        ["999"] = "capsule",
    };

    [Fact]
    public void Every_corner_radius_is_on_the_scale_or_a_documented_exception()
    {
        var offScale = Directory
            .EnumerateFiles(AppDirectory(), "*.axaml", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(file => Regex
                .Matches(File.ReadAllText(file), @"CornerRadius(?:="")(\d+)""|Property=""CornerRadius"" Value=""(\d+)""")
                .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
                .Where(value => !OffScale.ContainsKey(value))
                .Select(value => $"{value} in {Path.GetFileName(file)}"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(offScale.Length == 0,
            "Literal corner radii that are neither on the scale nor a documented exception: "
            + string.Join(", ", offScale)
            + ". Use {StaticResource RadiusSm|Radius|RadiusLg}, or add the value to OffScale with the reason.");
    }

    [Fact]
    public void The_scale_itself_is_defined()
    {
        // Guards the other direction: a test that only forbids literals would also pass against an app
        // that had lost the tokens they were replaced with.
        var app = File.ReadAllText(Path.Combine(AppDirectory(), "App.axaml"));

        foreach (var key in new[] { "RadiusSm", "Radius", "RadiusLg", "EaseDuration", "Ease" })
            Assert.Contains($"x:Key=\"{key}\"", app, StringComparison.Ordinal);
    }

    private static string AppDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            var app = Path.Combine(dir.FullName, "src", "Kontena.App");
            if (File.Exists(Path.Combine(app, "App.axaml")))
                return app;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not find src/Kontena.App from the test output directory.");
    }
}
