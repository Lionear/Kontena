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
/// A long connection name in the switcher is cut off inside its row, not run past it (KON-410).
/// Compared against the row rather than a pixel width, because headless text is measured with a stub.
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class SwitcherRowTrimTests(HeadlessSessionFixture headless)
{
    private const string LongName = "tardis-test-cluster-with-a-name-that-is-far-longer-than-any-switcher-row";

    private static readonly FakeEngineProvider Long = new("fake-long", LongName, "T");

    [Fact]
    public Task A_long_name_stays_inside_its_switcher_row() =>
        headless.Session.Dispatch(
            () =>
            {
                var store = new SettingsStore(
                    Path.Combine(Path.GetTempPath(), $"kontena-switchertrim-{Guid.NewGuid():N}.json"));
                var settings = new KontenaSettings { Onboarded = true, Startup = StartupBackend.LastUsed, LastBackend = Long.Backend };
                var vm = new MainWindowViewModel(
                    new BackendRegistry([Long]), store, settings,
                    updateService: new FakeUpdateService(), probeGrace: TimeSpan.FromMilliseconds(100));

                var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
                window.Show();
                Assert.True(Eventually(() => vm.Engines.Count == 1), "the switcher never settled");

                var pill = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "BackendPill");
                var flyout = Assert.IsType<Flyout>(pill.Flyout);
                flyout.ShowAt(pill);
                Dispatcher.UIThread.RunJobs();

                var content = Assert.IsAssignableFrom<Control>(flyout.Content);
                var row = content.GetVisualDescendants().OfType<Button>().First(b => b.DataContext is EngineOption);
                var name = row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == LongName);

                Assert.True(name.Bounds.Width < row.Bounds.Width, $"name {name.Bounds.Width} wide in a {row.Bounds.Width} row");
                return Task.CompletedTask;
            },
            CancellationToken.None);

    private static bool Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
            {
                Dispatcher.UIThread.RunJobs();
                return true;
            }

            Thread.Sleep(20);
        }

        return false;
    }
}
