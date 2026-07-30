using System.Text.RegularExpressions;

namespace Kontena.App.Tests;

/// <summary>
/// Every icon a view asks for is an icon that exists.
/// <para>
/// Worth a test because of how this fails: a resource key with no geometry behind it does not throw and
/// does not warn — the <c>Path</c> draws nothing and the button ships with a hole where its icon was.
/// Only someone looking at that screen finds it. It happened with <c>IconExternal</c>, which was very
/// nearly <c>IconExternalLink</c>, and a substring check said it was fine.
/// </para>
/// </summary>
public sealed class IconResourceTests
{
    [Fact]
    public void Every_icon_a_view_uses_is_defined()
    {
        var defined = Regex
            .Matches(File.ReadAllText(Path.Combine(AppDirectory(), "Icons.axaml")), @"x:Key=""(Icon\w*)""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(defined);

        var missing = Directory
            .EnumerateFiles(AppDirectory(), "*.axaml", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(file => Regex
                .Matches(File.ReadAllText(file), @"(?:Static|Dynamic)Resource (Icon\w*)\}")
                .Select(m => m.Groups[1].Value)
                .Where(key => !defined.Contains(key))
                .Select(key => $"{key} in {Path.GetFileName(file)}"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(missing.Length == 0, "Icons used but never defined: " + string.Join(", ", missing));
    }

    private static string AppDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            var app = Path.Combine(dir.FullName, "src", "Kontena.App");
            if (File.Exists(Path.Combine(app, "Icons.axaml")))
                return app;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not find src/Kontena.App from the test output directory.");
    }
}
