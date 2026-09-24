using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.App.Views;
using Kontena.Core.Models;
using Kontena.Engines;
using Kontena.Engines.Fakes;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// The settings dialog is the same size whichever section is open (KON-440).
/// <para>
/// Sections differ wildly in how much they hold, and the overlay used to take its size from whatever
/// was in it — so navigating the sub-nav resized the window under the pointer. Sizes are compared
/// against each other rather than against a number: this assembly measures text with a stub, so what
/// a section is worth in pixels here is not what it is worth in the app, but "the same" still is.
/// </para>
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class SettingsDialogSizeTests(HeadlessSessionFixture headless)
{
    // Every section the sub-nav can reach, including the three that used to come up short: before
    // KON-440 this assembly measured Updates at 420 high and Registries at 502 where General was 720,
    // and Tools 17px narrower than the rest.
    private static readonly string[] Categories =
    [
        "general", "engines", "registries", "updates",
        "extensions", "tools", "clusters", "remote-clusters",
    ];

    [Fact]
    public Task Every_section_leaves_the_dialog_the_same_size() => headless.Session.Dispatch(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"kontena-settings-size-{Guid.NewGuid():N}.json");

        try
        {
            var store = new SettingsStore(path);
            var settings = new KontenaSettings();
            store.Save(settings);

            // The store is the temp one, so nothing here reads or writes the real settings file.
            var shell = new MainWindowViewModel(new BackendRegistry([new FakeEngineProvider()]), store, settings);
            var window = new MainWindow { DataContext = shell, Width = 1200, Height = 900 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // Startup builds its own page asynchronously; this one is put in afterwards so the test
            // decides which section is open rather than racing whatever the probe round settled on.
            shell.SettingsPage = new SettingsViewModel(store, settings, [], new SettingsContext
            {
                Adapters = AdapterCatalog.All([]),
                Autostart = new UnsupportedAutostart(),
                Secrets = new UnavailableSecretStore(),
            });
            shell.IsSettingsOpen = true;

            var sizes = new List<(string Category, Avalonia.Size Size)>();

            foreach (var category in Categories)
            {
                shell.SettingsPage.Category = category;

                for (var i = 0; i < 5; i++)
                    Dispatcher.UIThread.RunJobs();

                var view = Assert.Single(window.GetVisualDescendants().OfType<SettingsView>());
                sizes.Add((category, view.Bounds.Size));
            }

            Assert.All(sizes, s => Assert.True(
                s.Size == sizes[0].Size,
                $"{s.Category} draws at {s.Size}, {sizes[0].Category} at {sizes[0].Size}"));

            // A dialog that never got laid out would also be "the same size" everywhere.
            Assert.True(sizes[0].Size.Width > 0 && sizes[0].Size.Height > 0);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }

        return Task.CompletedTask;
    }, CancellationToken.None);
}
