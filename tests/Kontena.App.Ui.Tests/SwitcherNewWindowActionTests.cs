using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.App.Views;
using Kontena.Core.Models;
using Kontena.Engines;
using Kontena.Engines.Fakes;
using Kontena.Sdk;
using Kontena.Sdk.Models;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// A connection in the switcher can be opened in a window of its own (KON-424).
/// <para>
/// Against the rendered flyout, because that is where this lives: the row template is what decides
/// which backends offer it, and a view-model test would pass with the button deleted. What the second
/// shell then opens is <c>NewBackendWindowTests</c>'s.
/// </para>
/// <para>
/// Not asserted here: that pressing it leaves this window on the backend it was on. A press inside a
/// popup does not reach the button headlessly — the same limitation that keeps the screenshot harness
/// out of this flyout — so a test of it would be green either way. It rests on Avalonia's own rule
/// instead: the inner button marks the press handled, and the row around it never sees it.
/// </para>
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class SwitcherNewWindowActionTests(HeadlessSessionFixture headless)
{
    private const string Tip = "Open in a new window";

    /// <summary>The engine this window is on, the one beside it, and one that does not answer.</summary>
    private static readonly FakeEngineProvider Open = new("fake-open", "Open engine", "O");
    private static readonly FakeEngineProvider Other = new("fake-other", "Other engine", "T");

    [Fact]
    public Task Only_a_backend_that_answered_offers_a_window_of_its_own() =>
        headless.Session.Dispatch(
            () =>
            {
                var (vm, rows) = Switcher();

                // Every row's template holds the button; IsVisible is what decides. A window opened on
                // a backend that did not answer would show nothing but the down card, and asking it
                // again is what its row already does. The active row keeps its button too — "another
                // window on what I am looking at" is the same request.
                Assert.Equal(
                    [Open.Backend, Other.Backend],
                    NewWindowButtons(rows).Select(b => ((EngineOption)b.DataContext!).Backend).Order());

                Assert.Equal(Open.DisplayName, vm.EngineName);

                return Task.CompletedTask;
            },
            CancellationToken.None);

    /// <summary>A shell on <see cref="Open"/>, with the switcher flyout up.</summary>
    private static (MainWindowViewModel Vm, Visual Rows) Switcher()
    {
        var store = new SettingsStore(
            Path.Combine(Path.GetTempPath(), $"kontena-switcherwindow-{Guid.NewGuid():N}.json"));

        var settings = new KontenaSettings
        {
            Onboarded = true,
            Startup = StartupBackend.LastUsed,
            LastBackend = Open.Backend,
        };

        // Three rows, one of each kind, so what is asserted is the difference between them rather than
        // a count that would also come out of an empty flyout.
        var vm = new MainWindowViewModel(
            new BackendRegistry([Open, Other, new DeadProvider()]), store, settings,
            updateService: new FakeUpdateService(),
            probeGrace: TimeSpan.FromMilliseconds(100));

        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();

        Assert.True(
            Eventually(() => vm.Engines.Count == 3 && vm.EngineName == Open.DisplayName),
            "the switcher never settled");

        var pill = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "BackendPill");
        var flyout = Assert.IsType<Flyout>(pill.Flyout);
        flyout.ShowAt(pill);
        Dispatcher.UIThread.RunJobs();

        // The flyout's own content, which is in the popup's tree once it is up. The popup host itself
        // is internal to Avalonia; this is the same subtree by another door.
        return (vm, Assert.IsAssignableFrom<Visual>(flyout.Content));
    }

    /// <summary>A backend that is known and does not answer — the switcher's other kind of row.</summary>
    private sealed class DeadProvider : IBackendProvider
    {
        public string Backend => "dead";
        public string DisplayName => "Stopped engine";
        public string Chip => "S";
        public BackendKind Kind => BackendKind.Engine;
        public IBackend CreateBackend() => new DeadBackend();
    }

    private sealed class DeadBackend : IBackend
    {
        public string Backend => "dead";
        public ValueTask PingAsync(CancellationToken ct = default) => throw new IOException("nothing there");
        public ValueTask<BackendInfo> GetInfoAsync(CancellationToken ct = default) =>
            throw new IOException("nothing there");
    }

    private static IEnumerable<Button> NewWindowButtons(Visual root) =>
        root.GetVisualDescendants()
            .OfType<Button>()
            .Where(b => ToolTip.GetTip(b) as string == Tip && b.IsEffectivelyVisible);

    /// <summary>
    /// Pump until the probe round has landed, without leaving the UI thread. The session dispatches a
    /// synchronous body — an <c>async</c> one returns at its first await and takes the assertions after
    /// it out of the test with it, which is a green test that ran nothing.
    /// </summary>
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
