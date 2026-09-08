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

    private static Window Show(SettingsViewModel page)
    {
        var window = new Window
        {
            Width = 1200,
            Height = 900,
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

            Assert.All(switches, t => Assert.True(t.IsChecked));
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
