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
/// Connections in the switcher can be put in the user's own order, and the order is kept (KON-410).
/// <para>
/// Through the rendered flyout, because the buttons are what makes it reachable by keyboard; a press
/// inside a popup does not arrive headlessly (see <c>SwitcherNewWindowActionTests</c>), so each
/// button's own command is run instead.
/// </para>
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class SwitcherReorderTests(HeadlessSessionFixture headless)
{
    private static readonly FakeEngineProvider First = new("fake-first", "First engine", "F");
    private static readonly FakeEngineProvider Second = new("fake-second", "Second engine", "S");

    [Fact]
    public Task Moving_a_row_down_reorders_the_switcher_and_survives_a_restart() =>
        headless.Session.Dispatch(
            () =>
            {
                var path = Path.Combine(Path.GetTempPath(), $"kontena-reorder-{Guid.NewGuid():N}.json");
                var (vm, rows) = Switcher(path);

                Assert.Equal([First.Backend, Second.Backend], vm.Engines.Select(e => e.Backend));

                var down = MoveButtons(rows, First.Backend, "Move down");
                Assert.True(down.IsEffectivelyEnabled);
                Assert.False(MoveButtons(rows, First.Backend, "Move up").IsEffectivelyEnabled);
                down.Command!.Execute(null);

                Assert.Equal([Second.Backend, First.Backend], vm.Engines.Select(e => e.Backend));
                Assert.Equal([Second.Backend, First.Backend], new SettingsStore(path).Load().InBackendOrder([First.Backend, Second.Backend]));

                // A fresh shell on the same settings file opens in the arranged order.
                var (again, _) = Switcher(path);
                Assert.Equal([Second.Backend, First.Backend], again.Engines.Select(e => e.Backend));
                return Task.CompletedTask;
            },
            CancellationToken.None);

    private static (MainWindowViewModel Vm, Control Rows) Switcher(string path)
    {
        var store = new SettingsStore(path);
        var settings = store.Load() with { Onboarded = true, Startup = StartupBackend.LastUsed, LastBackend = First.Backend };
        var vm = new MainWindowViewModel(
            new BackendRegistry([First, Second]), store, settings,
            updateService: new FakeUpdateService(), probeGrace: TimeSpan.FromMilliseconds(100));

        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        Assert.True(Eventually(() => vm.Engines.Count == 2 && vm.Engines.All(e => e.IsConnected)), "the switcher never settled");

        var pill = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "BackendPill");
        var flyout = Assert.IsType<Flyout>(pill.Flyout);
        flyout.ShowAt(pill);
        Dispatcher.UIThread.RunJobs();

        return (vm, Assert.IsAssignableFrom<Control>(flyout.Content));
    }

    /// <summary>By tooltip, which is also the button's accessible name.</summary>
    private static Button MoveButtons(Control rows, string backend, string tip) =>
        rows.GetVisualDescendants().OfType<Button>()
            .Single(b => b.DataContext is EngineOption o && o.Backend == backend && Equals(ToolTip.GetTip(b), tip));

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
