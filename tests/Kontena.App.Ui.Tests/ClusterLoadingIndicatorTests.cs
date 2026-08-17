using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kontena.App.ViewModels;
using Kontena.App.Views;
using Kontena.Core.Orchestration.Fakes;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// That every cluster page which fetches has something on screen saying so (KON-375).
/// <para>
/// The indicator is one line of XAML per view, which is exactly the kind of line that gets dropped in
/// a redesign without anything failing: a page with no progress bar builds, renders and reads as a
/// cluster that is simply empty. So the assertion is the binding — flip <c>IsLoading</c> and the bar
/// appears — rather than the presence of a control, which would still pass on a bar bound to nothing.
/// </para>
/// <para>
/// No pixel assertions here beyond a control being visible: headless Avalonia measures with a stub
/// glyph, so what the bar is worth on screen means nothing.
/// </para>
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class ClusterLoadingIndicatorTests(HeadlessSessionFixture headless)
{
    /// <summary>Show a page on its own and hand back the indeterminate bars in it.</summary>
    private static ProgressBar[] Bars(Control view, object viewModel)
    {
        view.DataContext = viewModel;

        var window = new Window { Content = view, Width = 900, Height = 700 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        return [.. view.GetVisualDescendants().OfType<ProgressBar>().Where(b => b.IsIndeterminate)];
    }

    /// <param name="startLoading">
    /// Flips the page's own loading flag. Passed rather than reached for: the four pages do not share
    /// a base class that has one, which is the whole reason each carries its own line of XAML.
    /// </param>
    private static void AssertFollowsIsLoading(Control view, object viewModel, Action startLoading)
    {
        var bars = Bars(view, viewModel);

        // Loaded already — the fake answers synchronously — so nothing should be claiming otherwise.
        Assert.DoesNotContain(bars, b => b.IsVisible);

        startLoading();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(bars, b => b.IsVisible);
    }

    [Fact]
    public Task The_overview_says_when_it_is_reading_the_cluster() =>
        headless.Session.Dispatch(
            () =>
            {
                var page = new ClusterOverviewViewModel(new FakeClusterEngine());
                AssertFollowsIsLoading(new ClusterOverviewView(), page, () => page.IsLoading = true);
            },
            CancellationToken.None);

    [Fact]
    public Task The_workloads_dashboard_says_when_it_is_reading_the_cluster() =>
        headless.Session.Dispatch(
            () =>
            {
                var page = new ClusterWorkloadsDashboardViewModel(new FakeClusterEngine(), null);
                AssertFollowsIsLoading(new ClusterWorkloadsDashboardView(), page, () => page.IsLoading = true);
            },
            CancellationToken.None);

    [Fact]
    public Task Config_maps_says_when_it_is_reading_the_cluster() =>
        headless.Session.Dispatch(
            () =>
            {
                var page = new ClusterConfigMapsViewModel(new FakeClusterEngine(), null);
                AssertFollowsIsLoading(new ClusterConfigMapsView(), page, () => page.IsLoading = true);
            },
            CancellationToken.None);

    [Fact]
    public Task Secrets_says_when_it_is_reading_the_cluster() =>
        headless.Session.Dispatch(
            () =>
            {
                var page = new ClusterSecretsViewModel(new FakeClusterEngine(), null);
                AssertFollowsIsLoading(new ClusterSecretsView(), page, () => page.IsLoading = true);
            },
            CancellationToken.None);

    /// <summary>
    /// The shell's own bar, for the wait no page can show: picking a namespace reads the cluster
    /// before the page that would carry a spinner has been built.
    /// </summary>
    [Fact]
    public Task The_shell_says_when_it_is_reading_the_cluster_between_pages() =>
        headless.Session.Dispatch(
            () =>
            {
                var shell = new MainWindowViewModel { IsClusterMode = true, IsReady = true };
                var window = new MainWindow { DataContext = shell };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                var bar = window.GetVisualDescendants().OfType<ProgressBar>()
                    .Single(b => b.IsIndeterminate && b.Height == 2);

                Assert.False(bar.IsVisible);

                shell.IsReadingCluster = true;
                Dispatcher.UIThread.RunJobs();

                Assert.True(bar.IsVisible);
            },
            CancellationToken.None);
}
