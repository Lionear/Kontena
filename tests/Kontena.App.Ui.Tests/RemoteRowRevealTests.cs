using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.App.Views;
using Kontena.Core.Models;
using Kontena.App;
using Kontena.Sdk.Models;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// "Manage below" takes a detected remote to its own row, further down the same page (KON-264).
/// <para>
/// Against the rendered view because the interesting half only exists there: the view model raises an
/// event, and everything after it — finding the container, scrolling to it, marking it — is the
/// code-behind's. A view model test would pass with that wiring deleted.
/// </para>
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class RemoteRowRevealTests(HeadlessSessionFixture headless)
{
    private HeadlessUnitTestSession Session => headless.Session;

    private static (Window Window, SettingsViewModel Page, string Path) Page()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kontena-reveal-{Guid.NewGuid():N}.json");
        var store = new SettingsStore(path);
        var remote = new RemoteEngine("r1", "Build server", RemoteEngineTransport.Ssh, "build-01", 22, "deploy");
        var settings = new KontenaSettings { RemoteEngines = [remote] };

        store.Save(settings);

        var page = new SettingsViewModel(
            store,
            settings,
            // The inventory above: Docker, plus the same remote the list below holds.
            [
                new EngineListItem("docker", "Docker", new BackendChipInfo("D"), "", true, false),
                new EngineListItem(
                    remote.Backend, "Build server", new BackendChipInfo("R"), remote.Endpoint,
                    false, false, IsRemote: true),
            ],
            new SettingsContext
            {
                Autostart = new UnsupportedAutostart(),
                Secrets = new UnavailableSecretStore(),
            })
        {
            Category = "engines",
        };

        var window = new Window
        {
            Width = 1200,
            Height = 900,
            Content = new SettingsView { DataContext = page },
        };

        window.Show();

        for (var i = 0; i < 5; i++)
            Dispatcher.UIThread.RunJobs();

        return (window, page, path);
    }

    [Fact]
    public Task Only_a_remote_row_offers_the_link_and_it_carries_its_backend() => Session.Dispatch(() =>
    {
        var (window, _, path) = Page();

        try
        {
            // Every row's template holds the button; IsVisible is what decides. Docker has no row
            // below to point at, so it must not show one — the whole reason the inventory carries no
            // actions of its own — and a hidden control is still a descendant, so this asks what is
            // actually on screen rather than what exists.
            var link = Assert.Single(window.GetVisualDescendants()
                .OfType<Button>()
                .Where(b => b.Content as string == "Manage below" && b.IsEffectivelyVisible));

            Assert.NotNull(link.Command);
            Assert.Equal("docker-remote:r1", link.CommandParameter);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }

        return Task.CompletedTask;
    }, CancellationToken.None);

    [Fact]
    public Task Pressing_it_marks_the_remotes_own_row() => Session.Dispatch(() =>
    {
        var (window, page, path) = Page();

        try
        {
            var link = window.GetVisualDescendants()
                .OfType<Button>()
                .Single(b => b.Content as string == "Manage below" && b.IsEffectivelyVisible);

            link.Command!.Execute(link.CommandParameter);

            for (var i = 0; i < 5; i++)
                Dispatcher.UIThread.RunJobs();

            var rows = window.GetVisualDescendants()
                .OfType<ItemsControl>()
                .Single(c => c.Name == "RemoteRows");

            var container = rows.ContainerFromIndex(0);

            Assert.NotNull(container);
            // The class the style tints on. It is removed again on a timer, which is why this asserts
            // right after the press rather than waiting.
            Assert.Contains("revealed", container.Classes);
            Assert.Single(page.RemoteEngines);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }

        return Task.CompletedTask;
    }, CancellationToken.None);
}
