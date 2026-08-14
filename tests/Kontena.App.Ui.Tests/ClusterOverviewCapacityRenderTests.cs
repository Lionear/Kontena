using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kontena.App.ViewModels;
using Kontena.App.Views;
using Kontena.Core.Orchestration.Fakes;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// That the two capacity tiles and the node table's memory column are actually on the overview
/// (KON-378). The view-model side is covered in <c>Kontena.App.Tests</c>; what only a rendered page
/// can catch is a column added to the header and forgotten in the row template, or a value bound to a
/// property that does not exist — both of which build and draw a page that merely looks unfinished.
/// <para>
/// Text and cell placement only. Headless Avalonia measures with a stub glyph, so nothing here says
/// anything about widths (KON-332).
/// </para>
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class ClusterOverviewCapacityRenderTests(HeadlessSessionFixture headless)
{
    [Fact]
    public Task The_overview_shows_the_cluster_ceiling_and_per_node_memory() =>
        headless.Session.Dispatch(
            () =>
            {
                var page = new ClusterOverviewViewModel(new FakeClusterEngine());
                var view = new ClusterOverviewView { DataContext = page };

                var window = new Window { Content = view, Width = 1200, Height = 800 };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                var text = view.GetVisualDescendants().OfType<TextBlock>()
                    .Select(t => t.Text ?? string.Empty).ToList();

                // The fake answers synchronously, so the page is loaded by now — asserted, because
                // every check below would pass just as happily against a page that read nothing.
                Assert.NotEmpty(page.Nodes);
                Assert.Equal("12", page.MaxCpu);

                // The tiles: both labels, and both values beside them rather than an empty card.
                Assert.Contains("Max CPUs", text);
                Assert.Contains("Max Memory", text);
                Assert.Contains(page.MaxCpu, text);
                Assert.Contains(page.MaxMemory, text);

                // The column: the header, and the same cell filled on every row of the table.
                Assert.Contains("MEMORY", text);
                Assert.All(page.Nodes, n => Assert.Contains(n.Memory, text));
            },
            CancellationToken.None);
}
