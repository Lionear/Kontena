using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kontena.App.ViewModels;
using Kontena.App.Views;
using Kontena.Core.Models;
using Kontena.Core.Orchestration.Fakes;
using Path = Avalonia.Controls.Shapes.Path;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// A pod in trouble is marked on the row itself, not only in the words of the status column
/// (KON-415).
/// <para>
/// Against the rendered view because the mark is three things that have to arrive together — a wash
/// on the row, the warning glyph in place of the status dot, and the reason in red — and a class
/// binding that never fires still compiles and still leaves a row that reads as ordinary text.
/// </para>
/// <para>
/// <b>What this deliberately does not assert:</b> anything measured in pixels. Headless Avalonia
/// draws through a text stub, so sizes here are the stub's and not the app's. These check presence in
/// the visual tree, the classes that carry the colour, and what the cell is bound to.
/// </para>
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class PodTroubleMarkRenderTests(HeadlessSessionFixture headless)
{
    private static Window Show(object view)
    {
        var window = new Window { Width = 1400, Height = 900, Content = view };

        window.Show();

        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        return window;
    }

    private static List<Border> TroubleRows(Window window) =>
        [.. window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("trouble"))];

    private static List<TextBlock> TroubleText(Window window) =>
        [.. window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Classes.Contains("statuscell") && t.Classes.Contains("trouble"))];

    // Effectively visible, not IsVisible: on the detail page the badge is hidden by the border around
    // it, and a glyph whose own flag is still true would count as drawn.
    private static List<Path> Glyphs(Window window) =>
        [.. window.GetVisualDescendants().OfType<Path>()
            .Where(p => p.Classes.Contains("trouble") && p.IsEffectivelyVisible)];

    [Fact]
    public Task The_pods_list_marks_the_rows_that_are_in_trouble_and_leaves_the_rest_alone() =>
        headless.Session.Dispatch(
            () =>
            {
                var page = new ClusterPodsViewModel(new FakeClusterEngine(), "app");
                page.LoadAsync().GetAwaiter().GetResult();

                var expected = page.Items.Where(r => r.HasTrouble).ToList();

                // The fixture has to contain both, or the test cannot tell a working binding from one
                // that marks everything.
                Assert.NotEmpty(expected);
                Assert.Contains(page.Items, r => !r.HasTrouble);

                var window = Show(new ClusterPodsView { DataContext = page });

                Assert.Equal(expected.Count, TroubleRows(window).Count);
                Assert.Equal(expected.Count, Glyphs(window).Count);

                // The reason is what the cell reads, not the phase: "Running" is the one thing that
                // does not mention a container in CrashLoopBackOff.
                var marked = TroubleText(window);
                Assert.Equal(expected.Count, marked.Count);
                Assert.Equal(
                    [.. expected.Select(r => r.Trouble).Order()],
                    [.. marked.Select(t => t.Text).Order()]);
            },
            CancellationToken.None);

    [Fact]
    public Task The_related_pods_list_marks_them_the_same_way() => headless.Session.Dispatch(
        () =>
        {
            var cluster = new FakeClusterEngine();
            var redis = cluster.ListWorkloadsAsync(null, "app").GetAwaiter().GetResult().First(w => w.Name == "redis");
            var page = new ClusterWorkloadDetailViewModel(cluster, redis);
            page.RefreshPodsAsync().GetAwaiter().GetResult();

            Assert.Contains(page.Pods, p => p.HasTrouble);

            var window = Show(new RelatedPodsView { DataContext = page });

            Assert.Equal(page.Pods.Count(p => p.HasTrouble), TroubleRows(window).Count);
            Assert.Equal(page.Pods.Count(p => p.HasTrouble), Glyphs(window).Count);
        },
        CancellationToken.None);

    [Fact]
    public Task The_detail_page_carries_the_mark_the_list_row_had() => headless.Session.Dispatch(
        () =>
        {
            var cluster = new FakeClusterEngine();
            var pod = cluster.ListPodsAsync("app").GetAwaiter().GetResult().First(p => p.Name == "redis-0c1e");
            using var page = new ClusterPodDetailViewModel(cluster, pod, new TerminalFont("JetBrains Mono", 13, false));

            Assert.True(page.HasTrouble);

            var window = Show(new ClusterPodDetailView { DataContext = page });

            Assert.Single(Glyphs(window));
            Assert.Contains(TroubleText(window), t => t.Text == page.Trouble);
        },
        CancellationToken.None);

    private static List<TextBlock> OftenRestartedCells(Window window) =>
        [.. window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Classes.Contains("restarts") && t.Classes.Contains("often"))];

    [Fact]
    public Task A_pod_that_restarted_often_but_is_healthy_is_pointed_at_without_being_called_broken() =>
        headless.Session.Dispatch(
            () =>
            {
                // KON-442. The two halves are the whole point and have to be asserted together: the
                // row must stay unmarked (no wash, no triangle, "Running" in the status cell) *and*
                // the count must still stand out. Either one alone is a state we already had.
                var page = new ClusterPodsViewModel(new FakeClusterEngine(), "app");
                page.LoadAsync().GetAwaiter().GetResult();

                var restarted = page.Items.Single(r => r.RestartedOften && !r.HasTrouble);

                Assert.Equal("Running", restarted.StatusLine);
                Assert.Null(restarted.Trouble);

                var window = Show(new ClusterPodsView { DataContext = page });

                // Exactly the counts that earned it, and no others — a binding that fires on the wrong
                // thing marks every row and would pass a weaker assertion. The crash-looping pod is in
                // here too: it has restarted plenty, and the two readings are independent on purpose.
                var marked = OftenRestartedCells(window);
                Assert.Equal(
                    [.. page.Items.Where(r => r.RestartedOften).Select(r => r.Restarts).Order()],
                    [.. marked.Select(t => t.Text ?? string.Empty).Order()]);
                Assert.Contains(marked, t => t.Text == restarted.Restarts);

                // The reassurance is for the pod it is true of. The crash-looping pod has restarted
                // often as well, and "running normally now" there would contradict its own red row.
                Assert.NotNull(restarted.RestartsTip);
                Assert.All(page.Items.Where(r => r.HasTrouble), r => Assert.Null(r.RestartsTip));

                // And this pod adds no row to the marked set: the washes on screen are still only the
                // ones the trouble rule asked for.
                Assert.Equal(page.Items.Count(r => r.HasTrouble), TroubleRows(window).Count);
            },
            CancellationToken.None);

    [Fact]
    public Task A_pod_that_restarted_once_is_left_alone() => headless.Session.Dispatch(
        () =>
        {
            // The counterweight: without this, a binding that marks every count would pass the test
            // above just as happily.
            var page = new ClusterPodsViewModel(new FakeClusterEngine(), "monitoring");
            page.LoadAsync().GetAwaiter().GetResult();

            Assert.All(page.Items, r => Assert.False(r.RestartedOften));

            var window = Show(new ClusterPodsView { DataContext = page });

            Assert.Empty(OftenRestartedCells(window));
        },
        CancellationToken.None);

    [Fact]
    public Task A_healthy_pod_gets_no_mark_anywhere() => headless.Session.Dispatch(
        () =>
        {
            var cluster = new FakeClusterEngine();
            var pod = cluster.ListPodsAsync("app").GetAwaiter().GetResult().First(p => p.Name == "api-7d9c");
            using var page = new ClusterPodDetailViewModel(cluster, pod, new TerminalFont("JetBrains Mono", 13, false));

            Assert.False(page.HasTrouble);

            var window = Show(new ClusterPodDetailView { DataContext = page });

            Assert.Empty(Glyphs(window));
        },
        CancellationToken.None);
}
