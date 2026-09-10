using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kontena.App;
using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.App.Views;
using Kontena.Core.Models;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// Settings › Extensions draws a card per adapter, and its switch is bound to that row (KON-283).
/// <para>
/// Against the rendered view because the cards live in an <c>ItemsControl</c> with its own
/// <c>ItemsPanel</c>: the view model half is covered by <c>ExtensionsSettingsTests</c>, and what is
/// left to get wrong is whether the template is reached at all and which object its switch writes to.
/// </para>
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class ExtensionsPageRenderTests(HeadlessSessionFixture headless)
{
    private HeadlessUnitTestSession Session => headless.Session;

    private static SettingsViewModel Page(SettingsStore store, KontenaSettings settings) =>
        new(store, settings, [], new SettingsContext
        {
            Adapters = AdapterCatalog.All([]),
            Autostart = new UnsupportedAutostart(),
            Secrets = new UnavailableSecretStore(),
        })
        {
            Category = "extensions",
        };

    private static Window Show(SettingsViewModel page, double width = 1200, double height = 900)
    {
        var window = new Window
        {
            Width = width,
            Height = height,
            Content = new SettingsView { DataContext = page },
        };

        window.Show();

        for (var i = 0; i < 5; i++)
            Dispatcher.UIThread.RunJobs();

        return window;
    }

    [Fact]
    public Task Every_adapter_gets_a_switch_bound_to_its_own_row() => Session.Dispatch(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"kontena-extensions-ui-{Guid.NewGuid():N}.json");

        try
        {
            var store = new SettingsStore(path);
            var settings = new KontenaSettings();
            store.Save(settings);

            var page = Page(store, settings);
            var window = Show(page);

            var switches = window.GetVisualDescendants()
                .OfType<ToggleSwitch>()
                .Where(t => t.DataContext is AdapterRow)
                .ToList();

            Assert.Equal(page.Adapters.Count, switches.Count);

            // One switch per row, and each writing to the row it is drawn on — not to the first.
            Assert.Equal(
                page.Adapters.Count,
                switches.Select(t => t.DataContext).Distinct().Count());

            // On for everything this machine can run. An adapter it cannot is drawn off and dead
            // (KON-468) — that half is asserted below.
            Assert.All(
                switches.Where(t => ((AdapterRow)t.DataContext!).CanToggle),
                t => Assert.True(t.IsChecked));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }

        return Task.CompletedTask;
    }, CancellationToken.None);

    [Fact]
    public Task A_card_shows_the_adapter_name_kind_and_where_it_came_from() => Session.Dispatch(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"kontena-extensions-ui-{Guid.NewGuid():N}.json");

        try
        {
            var store = new SettingsStore(path);
            var settings = new KontenaSettings();
            store.Save(settings);

            var window = Show(Page(store, settings));

            var texts = window.GetVisualDescendants()
                .OfType<TextBlock>()
                .Select(t => t.Text)
                .Where(t => t is { Length: > 0 })
                .ToList();

            Assert.Contains("Docker", texts);
            Assert.Contains("Container engine", texts);
            Assert.Contains("Orchestrator", texts);
            Assert.Contains("built-in", texts);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }

        return Task.CompletedTask;
    }, CancellationToken.None);

    /// <summary>
    /// An adapter this machine cannot run gets a card like any other, with the switch greyed out and
    /// the reason drawn beside it (KON-468). Against the rendered view because both are bindings the
    /// view model half cannot speak for: <c>IsEnabled</c> on the switch and <c>IsVisible</c> on the
    /// reason.
    /// <para>
    /// Drawn at the narrowest this dialog ever is — 836&#215;458, what a 900&#215;560 window leaves after
    /// the title bar and the overlay's margins (KON-440) — because that is where the reason is at risk.
    /// Two cards share the content column, so a sentence squeezed onto the switch's own row would have
    /// no width left here; its bounds are asserted rather than only its visibility, since a line
    /// trimmed to nothing is still <c>IsVisible</c>.
    /// </para>
    /// </summary>
    [Fact]
    public Task An_adapter_that_cannot_run_here_draws_a_dead_switch_and_its_reason() => Session.Dispatch(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"kontena-extensions-ui-{Guid.NewGuid():N}.json");

        try
        {
            var store = new SettingsStore(path);
            var settings = new KontenaSettings();
            store.Save(settings);

            var page = Page(store, settings);
            var window = Show(page, width: 836, height: 458);

            var switches = window.GetVisualDescendants()
                .OfType<ToggleSwitch>()
                .Where(t => t.DataContext is AdapterRow)
                .ToList();

            // Equality against the row rather than a hunt for Apple: what is asserted is that the
            // bindings follow the row, on a Mac and off it alike.
            Assert.NotEmpty(switches);
            Assert.All(switches, t =>
            {
                var row = (AdapterRow)t.DataContext!;

                Assert.Equal(row.CanToggle, t.IsEnabled);
                Assert.Equal(row.CanToggle, t.IsChecked);
            });

            var reasons = window.GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(b => b.DataContext is AdapterRow row && row.IsUnavailable
                            && b.Text == ((AdapterRow)b.DataContext).UnavailableReason)
                .ToList();

            Assert.Equal(page.Adapters.Count(r => r.IsUnavailable), reasons.Count);
            Assert.All(reasons, b =>
            {
                Assert.True(b.IsVisible);

                // Room to actually read it. Width alone is not enough: the sentence wraps, so a line
                // that fits on one row and one that needed two both have to have arrived somewhere.
                Assert.True(b.Bounds.Width > 100, $"the reason was drawn {b.Bounds.Width}px wide.");
                Assert.True(b.Bounds.Height > 0, "the reason was drawn with no height.");
            });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }

        return Task.CompletedTask;
    }, CancellationToken.None);

    /// <summary>
    /// The sub-nav entry has to exist, or the page is unreachable however well it renders.
    /// </summary>
    [Fact]
    public Task The_sub_nav_offers_extensions() => Session.Dispatch(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"kontena-extensions-nav-{Guid.NewGuid():N}.json");

        try
        {
            var store = new SettingsStore(path);
            var settings = new KontenaSettings();
            store.Save(settings);

            var page = Page(store, settings);
            page.Category = "general";
            var window = Show(page);

            var entry = window.GetVisualDescendants()
                .OfType<Button>()
                .FirstOrDefault(b => b.Classes.Contains("snav") && (b.CommandParameter as string) == "extensions");

            Assert.NotNull(entry);
            Assert.True(entry.IsVisible);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }

        return Task.CompletedTask;
    }, CancellationToken.None);
}
