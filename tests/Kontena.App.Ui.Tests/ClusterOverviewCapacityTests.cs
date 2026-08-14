using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kontena.App.ViewModels;
using Kontena.App.Views;
using Kontena.Core.Orchestration.Fakes;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// The overview's capacity tiles and the node table's memory column (KON-378).
/// <para>
/// What the view-model computes is tested next door, in <c>Kontena.App.Tests</c>. What can only go
/// wrong here is the wiring: a tile bound to a property that no longer exists, or a column added to
/// the header row and forgotten in the item template — both of which render as a page that simply
/// looks a little emptier than it should. So these assertions flip the source and watch the text
/// follow, rather than count controls.
/// </para>
/// <para>
/// No pixel assertions: headless Avalonia measures with a stub glyph, so column widths are worth
/// nothing here.
/// </para>
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class ClusterOverviewCapacityTests(HeadlessSessionFixture headless)
{
    private static ClusterOverviewView Shown(ClusterOverviewViewModel page)
    {
        var view = new ClusterOverviewView { DataContext = page };
        new Window { Content = view, Width = 1180, Height = 900 }.Show();
        Dispatcher.UIThread.RunJobs();

        return view;
    }

    private static string[] Texts(Control view) =>
        [.. view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty)];

    [Fact]
    public Task The_two_capacity_tiles_show_what_the_page_says_the_ceiling_is() =>
        headless.Session.Dispatch(
            () =>
            {
                var page = new ClusterOverviewViewModel(new FakeClusterEngine());
                var view = Shown(page);

                Assert.Contains("Max CPUs", Texts(view));
                Assert.Contains("Max Memory", Texts(view));

                // The fake's three Ready nodes allocate 4000m and 16 GiB each.
                Assert.Contains("12", Texts(view));
                Assert.Contains(Format.Size(48L * 1024 * 1024 * 1024), Texts(view));

                // And the tiles follow the page rather than having been filled in once.
                page.MaxCpus = "99";
                Dispatcher.UIThread.RunJobs();

                Assert.Contains("99", Texts(view));
            },
            CancellationToken.None);

    [Fact]
    public Task Every_node_row_renders_its_memory_beside_its_cpu() =>
        headless.Session.Dispatch(
            () =>
            {
                var page = new ClusterOverviewViewModel(new FakeClusterEngine());
                var view = Shown(page);
                var texts = Texts(view);

                Assert.Contains("MEMORY", texts);

                // One cell per row, not one header and an empty template.
                var cell = $"{Format.Size(6L * 1024 * 1024 * 1024)} / {Format.Size(16L * 1024 * 1024 * 1024)}";
                Assert.Equal(page.Nodes.Count, texts.Count(t => t == cell));
            },
            CancellationToken.None);
}
