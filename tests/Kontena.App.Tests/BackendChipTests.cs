using System.Text.RegularExpressions;
using Avalonia.Media;
using Kontena.Adapters.Docker;
using Kontena.Adapters.Kubernetes;
using Kontena.Adapters.Podman;
using Kontena.App;
using Kontena.App.Controls;
using Kontena.Sdk.Models;
using Kontena.Sdk;
using Kontena.Engines.Fakes;
using Kontena.Core.Models;
using Kontena.Core.Orchestration;

namespace Kontena.App.Tests;

/// <summary>
/// Backend chips carry the product's own mark, declared by the provider (KON-80).
/// <para>
/// Two things are worth pinning. Which backends claim a mark — the letter fallback is a decision for
/// the demo and remote entries, not an omission — and that a mark is legible on its own plate: a brand
/// colour is fixed while the theme is not, and measured, Podman's violet on dark and Docker's blue on
/// light both fail the 3:1 floor for a graphic that carries meaning.
/// </para>
/// </summary>
public sealed class BackendChipTests
{
    private static readonly (string Name, IBackendProvider Provider)[] WithMark =
    [
        ("Docker", new DockerEngineProvider()),
        ("Podman", new PodmanEngineProvider()),
        ("Kubernetes", new KubernetesClusterProvider("prod-eu-west")),
    ];

    [Fact]
    public void The_built_in_backends_declare_their_own_mark()
    {
        Assert.All(WithMark, b =>
        {
            var style = b.Provider.ChipStyle;
            Assert.NotNull(style);
            Assert.NotEmpty(style!.Glyph);
            Assert.Matches("^#[0-9A-Fa-f]{6}$", style.Accent);
        });
    }

    [Fact]
    public void A_demo_backend_and_a_remote_engine_keep_their_letter()
    {
        // Both on purpose. The demo backends are not a product and should not wear one's logo, and a
        // remote engine *is* Docker — the whale would erase the only thing telling it apart from the
        // local socket in the same switcher.
        // Through the interface, because ChipStyle has a default implementation there — which is the
        // point of it: an adapter written before this existed still compiles, and gets a letter.
        Assert.Null(((IBackendProvider)new FakeEngineProvider()).ChipStyle);
        Assert.Null(((IBackendProvider)new RemoteDockerEngineProvider(
            new RemoteEngine("abc", "build-01", RemoteEngineTransport.Ssh, "10.0.0.4"))).ChipStyle);
    }

    [Fact]
    public void Every_mark_clears_the_graphics_floor_on_every_surface_in_both_themes()
    {
        // The same measurement the palette tests make, against the plate rather than a token: the chip
        // is the mark on a 16% wash of its own colour, which is the case a flat palette check misses.
        var failures = new List<string>();

        foreach (var (name, provider) in WithMark)
        {
            var accent = provider.ChipStyle!.Accent;

            foreach (var (theme, surfaces) in Surfaces())
            {
                var dark = theme == "Dark";
                var ink = BackendChipInk.For(accent, dark);

                foreach (var (surfaceName, surface) in surfaces)
                {
                    var plate = BackendChipInk.Composite(
                        Color.Parse(accent), Color.Parse(surface), BackendChipInk.PlateOpacity);
                    var ratio = BackendChipInk.Contrast(ink, plate);

                    if (ratio < BackendChipInk.Floor)
                        failures.Add($"{theme}: {name} on {surfaceName} = {ratio:0.00}:1");
                }
            }
        }

        Assert.True(failures.Count == 0,
            "Marks below the 3:1 floor:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void An_ink_that_already_clears_is_left_at_the_brand_colour()
    {
        // The lift is a repair, not a filter: Docker's blue is legible on dark as it is, and a chip that
        // shifted it anyway would be showing a colour Docker never chose.
        Assert.Equal(Color.Parse(DockerBrand.Accent), BackendChipInk.For(DockerBrand.Accent, dark: true));

        // Podman's violet on dark is the case that needs it — and what comes back is lighter, not grey.
        var lifted = BackendChipInk.For(PodmanBrand.Accent, dark: true);
        Assert.NotEqual(Color.Parse(PodmanBrand.Accent), lifted);
        Assert.True(lifted.R > 0x89 && lifted.B > 0xA0, $"expected a lighter violet, got {lifted}");
    }

    [Fact]
    public void A_chip_resolves_from_a_backend_id_alone()
    {
        // What a container or activity row has to work with: an id, no provider.
        BackendChips.Learn([new DockerEngineProvider(), new KubernetesClusterProvider("prod-eu-west")]);

        Assert.Equal(DockerBrand.Glyph, BackendChips.For("docker").Glyph);

        // A cluster id carries its context, and a pod row only ever knows the whole id.
        Assert.Equal(KubernetesBrand.Glyph, BackendChips.For("kubernetes:some-other-context").Glyph);

        // A remote engine is its own family, so it falls back — which is the decision above, not a miss.
        var remote = BackendChips.For("docker-remote:abc");
        Assert.False(remote.HasGlyph);
        Assert.Equal("D", remote.Letter);

        // And a backend nobody declared anything for still has to be drawn.
        Assert.Equal("N", BackendChips.For("nomad").Letter);
        Assert.Equal("?", BackendChips.For("").Letter);
    }

    [Fact]
    public void Forgetting_a_provider_forgets_its_mark()
    {
        // Learn replaces rather than adds: a kubeconfig that was removed should stop contributing a
        // logo, or the chip outlives the backend it describes.
        BackendChips.Learn([new DockerEngineProvider(), new KubernetesClusterProvider("gone")]);
        Assert.True(BackendChips.For("kubernetes:gone").HasGlyph);

        BackendChips.Learn([new DockerEngineProvider()]);
        Assert.False(BackendChips.For("kubernetes:gone").HasGlyph);
        Assert.True(BackendChips.For("docker").HasGlyph);
    }

    // ── The surfaces a chip sits on, read out of the app's own palette ───────

    private static IEnumerable<(string Theme, List<(string Name, string Colour)> Surfaces)> Surfaces()
    {
        // Read from App.axaml rather than copied here: a chip has to stay legible when the palette
        // moves, and this is the list of grounds it actually lands on (sidebar, page, row, dialog).
        string[] wanted = ["Bg", "Surface", "Surface2", "SidebarBg"];
        var xaml = File.ReadAllText(AppXamlPath());

        foreach (Match theme in Regex.Matches(
                     xaml, @"<ResourceDictionary x:Key=""(Dark|Light)"">(.*?)</ResourceDictionary>",
                     RegexOptions.Singleline))
        {
            var surfaces = new List<(string, string)>();
            foreach (Match brush in Regex.Matches(
                         theme.Groups[2].Value, @"x:Key=""(\w+)""\s+Color=""(#[0-9A-Fa-f]{6})""(?!\s+Opacity)"))
            {
                if (wanted.Contains(brush.Groups[1].Value))
                    surfaces.Add((brush.Groups[1].Value, brush.Groups[2].Value));
            }

            Assert.Equal(wanted.Length, surfaces.Count);
            yield return (theme.Groups[1].Value, surfaces);
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
}
