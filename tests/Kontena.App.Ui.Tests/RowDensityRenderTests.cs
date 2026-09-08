using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.App.Views;
using Kontena.Core.Orchestration.Fakes;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// Compact density reaches the cluster lists, not just the engine lists (KON-441).
/// <para>
/// Against a rendered page rather than against <see cref="DensityApplier"/> alone, because the bug
/// was never in the applier: it swapped a resource that the cluster rows did not bind. A test that
/// only read the resource back out of the application would have passed the whole time the setting
/// did nothing on nineteen pages.
/// </para>
/// <para>
/// The numbers here are the two ends of the resource, not a measured row height — headless Avalonia
/// measures text with a stub, so what a row is actually tall is not assertable (KON-332). What is
/// assertable is that the row's spacing follows the setting instead of a literal.
/// </para>
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class RowDensityRenderTests(HeadlessSessionFixture headless)
{
    private static Window Show(object view)
    {
        var window = new Window { Width = 1400, Height = 900, Content = view };

        window.Show();
        Pump();

        return window;
    }

    private static void Pump()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>
    /// The row borders. Found by the row they carry rather than by their shape: the table header is
    /// a border with the same single bottom edge and the same 16px inset, and it does not follow the
    /// setting (neither does the engine tables' header).
    /// </summary>
    private static List<Border> Rows(Window window) =>
    [
        .. window.GetVisualDescendants().OfType<Border>()
            .Where(b => b.DataContext is PodRow && b.BorderThickness is { Bottom: 1, Top: 0 })
    ];

    [Fact]
    public Task Cluster_list_rows_tighten_when_compact_density_is_on() => headless.Session.Dispatch(
        () =>
        {
            var page = new ClusterPodsViewModel(new FakeClusterEngine(), "app");
            page.LoadAsync().GetAwaiter().GetResult();

            try
            {
                DensityApplier.Apply(compact: false);

                var window = Show(new ClusterPodsView { DataContext = page });
                var rows = Rows(window);

                // One per pod — if this is empty the selector below stopped matching a row and the
                // rest of the test would pass vacuously.
                Assert.Equal(page.Items.Count, rows.Count);
                Assert.All(rows, r => Assert.Equal(11, r.Padding.Top));

                DensityApplier.Apply(compact: true);
                Pump();

                Assert.All(Rows(window), r => Assert.Equal(6, r.Padding.Top));
            }
            finally
            {
                // The application is shared by every UI test in this assembly.
                DensityApplier.Apply(compact: false);
            }
        },
        CancellationToken.None);
}
