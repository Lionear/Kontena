using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kontena.Adapters.Docker;
using Kontena.Adapters.Podman;
using Kontena.App;
using Kontena.App.Controls;

// Aliased: System.IO.Path is in scope here through the test project's implicit usings.
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// A backend chip actually draws the mark it was given (KON-80).
/// <para>
/// In the app rather than against the resolver, for two reasons. The vendored path data has to be
/// drawable at all — a mark that fails to parse is a blank chip, and nothing else in the build would
/// say so — and the last chip bug was exactly a value the view never read: <c>Destructive</c> existed,
/// was set, and no scene consulted it (KON-126).
/// </para>
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class BackendChipRenderTests(HeadlessSessionFixture headless)
{
    private HeadlessUnitTestSession Session => headless.Session;

    private static Window Show(BackendChip chip)
    {
        var window = new Window { Width = 200, Height = 100, Content = chip };
        window.Show();
        Settle();
        return window;
    }

    private static void Settle()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    [Fact]
    public Task Every_vendored_mark_parses_and_is_drawn() =>
        Session.Dispatch(
            () =>
            {
                // All four, including Apple's, which has no adapter to be exercised through.
                var marks = new (string Name, string Glyph, string Accent)[]
                {
                    ("Docker", DockerBrand.Glyph, DockerBrand.Accent),
                    ("Podman", PodmanBrand.Glyph, PodmanBrand.Accent),
                    ("Kubernetes", Adapters.Kubernetes.KubernetesBrand.Glyph, Adapters.Kubernetes.KubernetesBrand.Accent),
                    ("Apple", AppleBrand.Glyph, AppleBrand.Accent),
                };

                foreach (var (name, glyph, accent) in marks)
                {
                    var chip = new BackendChip { Chip = new BackendChipInfo("X", glyph, accent), ChipSize = 26 };
                    Show(chip);

                    var path = chip.GetVisualDescendants().OfType<ShapePath>().SingleOrDefault();
                    Assert.NotNull(path);
                    Assert.NotNull(path!.Data);

                    // A path that parsed but enclosed nothing would still render an empty chip.
                    Assert.True(path.Data!.Bounds.Width > 0 && path.Data.Bounds.Height > 0,
                        $"{name}: mark has no area ({path.Data.Bounds})");

                    // And the letter is gone — a chip showing both would mean the fallback leaked.
                    Assert.Empty(chip.GetVisualDescendants().OfType<TextBlock>());
                }
            },
            CancellationToken.None);

    [Fact]
    public Task A_backend_without_a_mark_falls_back_to_its_letter() =>
        Session.Dispatch(
            () =>
            {
                var chip = new BackendChip { Chip = new BackendChipInfo("R"), ChipSize = 30 };
                Show(chip);

                Assert.Empty(chip.GetVisualDescendants().OfType<ShapePath>());
                Assert.Equal("R", chip.GetVisualDescendants().OfType<TextBlock>().Single().Text);
            },
            CancellationToken.None);

    [Fact]
    public Task Switching_theme_repaints_the_mark() =>
        Session.Dispatch(
            () =>
            {
                // The brand colour comes from a provider, so no DynamicResource can follow the theme for
                // it. If the chip did not rebuild, Podman's violet would stay at its light-theme value
                // and sit on its own plate at 2.2:1 in the dark one.
                var application = Application.Current!;
                var was = application.RequestedThemeVariant;

                try
                {
                    var chip = new BackendChip
                    {
                        Chip = new BackendChipInfo("P", PodmanBrand.Glyph, PodmanBrand.Accent),
                        ChipSize = 26,
                    };

                    application.RequestedThemeVariant = ThemeVariant.Light;
                    Show(chip);
                    var light = Ink(chip);

                    application.RequestedThemeVariant = ThemeVariant.Dark;
                    Settle();
                    var dark = Ink(chip);

                    Assert.NotEqual(light, dark);
                    Assert.True(BackendChipInk.Contrast(dark, Plate(PodmanBrand.Accent, "#1C2136"))
                        >= BackendChipInk.Floor);
                    Assert.True(BackendChipInk.Contrast(light, Plate(PodmanBrand.Accent, "#F0F3F6"))
                        >= BackendChipInk.Floor);
                }
                finally
                {
                    application.RequestedThemeVariant = was;
                }
            },
            CancellationToken.None);

    private static Color Ink(BackendChip chip) =>
        ((SolidColorBrush)chip.GetVisualDescendants().OfType<ShapePath>().Single().Fill!).Color;

    private static Color Plate(string accent, string surface) => BackendChipInk.Composite(
        Color.Parse(accent), Color.Parse(surface), BackendChipInk.PlateOpacity);
}
