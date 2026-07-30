using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Styling;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// Every resource key a view asks for resolves — in both themes (KON-218).
/// <para>
/// This class of mistake is silent by construction. A <c>DynamicResource</c> that resolves to nothing
/// does not throw and does not warn: the property keeps its default, so a missing brush is a border
/// that simply is not drawn. <c>BorderSoft</c> travelled through ten views that way, and
/// <c>AddBackendView</c> asked for <c>Raised</c> while the token is called <c>SurfaceRaised</c>.
/// </para>
/// <para>
/// Resolved against the running application rather than by reading <c>App.axaml</c>, because the app's
/// own dictionaries are not the only source: the Fluent theme supplies keys too, and a text matcher
/// would report every one of those as missing. Both themes are checked, since a dictionary is per
/// theme and a key present in only one is a screen that loses its colour when someone switches.
/// </para>
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class ResourceKeyTests(HeadlessSessionFixture headless)
{
    private HeadlessUnitTestSession Session => headless.Session;

    [Fact]
    public Task Every_resource_key_a_view_uses_resolves_in_both_themes() =>
        Session.Dispatch(
            () =>
            {
                var used = KeysUsedInViews();

                // A regex that matched nothing would make this test pass for the wrong reason.
                Assert.True(used.Count > 20, $"Only found {used.Count} resource references — the scan is broken.");

                var app = Application.Current;
                Assert.NotNull(app);

                var missing = new List<string>();

                foreach (var (key, file, ownFile) in used)
                {
                    // A view may define its own keys — a DataTemplate it hands to an ItemsControl, a
                    // one-off brush. Those never reach the application's dictionaries, so an app-level
                    // lookup would report them missing. Satisfied by a definition *in the same file*
                    // rather than anywhere: a key defined in one view and used in another does not
                    // resolve at runtime either, and an allow-list keyed on name alone would wave that
                    // through — which is how a guard stops guarding.
                    if (ownFile.Contains(key))
                        continue;

                    foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
                        if (!app!.TryGetResource(key, theme, out var value) || value is null)
                            missing.Add($"{key} ({theme}) in {file}");
                }

                Assert.True(missing.Count == 0,
                    "Resource keys used but never defined: " + string.Join(", ", missing));
            },
            default);

    /// <summary>
    /// Every <c>{StaticResource X}</c> / <c>{DynamicResource X}</c> in the app's XAML, with the set of
    /// keys the same file defines for itself.
    /// </summary>
    private static List<(string Key, string File, HashSet<string> OwnFile)> KeysUsedInViews()
    {
        var found = new List<(string, string, HashSet<string>)>();

        foreach (var file in Directory.EnumerateFiles(AppDirectory(), "*.axaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;

            var xaml = File.ReadAllText(file);

            var defined = Regex.Matches(xaml, @"x:Key=""(\w+)""")
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);

            foreach (Match match in Regex.Matches(xaml, @"\{(?:Static|Dynamic)Resource\s+(\w+)\s*\}"))
                found.Add((match.Groups[1].Value, Path.GetFileName(file), defined));
        }

        return found;
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
