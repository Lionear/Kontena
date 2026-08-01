using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kontena.App.ViewModels;
using Kontena.App.Views;
using Kontena.Core.Orchestration.Fakes;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// That the detail drawer actually draws (KON-307).
/// <para>
/// <c>DetailDrawerTests</c> covers what the shell does; this covers the half a build cannot
/// disagree with. A ContentControl whose template does not resolve renders the ViewLocator's
/// "Not Found:" placeholder, a missing icon resource throws at load, and a mistyped style selector
/// simply does nothing — none of which is a compile error. Build ≠ runtime has cost this repo before.
/// </para>
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class DetailDrawerRenderTests(HeadlessSessionFixture headless)
{
    private static (Window Window, MainWindowViewModel Shell) OpenDrawer()
    {
        var cluster = new FakeClusterEngine();
        var ns = cluster.ListNamespacesAsync().AsTask().GetAwaiter().GetResult()[0];

        var shell = new MainWindowViewModel { Detail = new ClusterNamespaceDetailViewModel(cluster, ns) };
        var window = new MainWindow { DataContext = shell };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        return (window, shell);
    }

    [Fact]
    public Task The_drawer_shows_the_detail_and_not_the_view_locator_placeholder() =>
        headless.Session.Dispatch(
            () =>
            {
                var (window, _) = OpenDrawer();

                Assert.Single(window.GetVisualDescendants().OfType<ClusterNamespaceDetailView>());
                Assert.DoesNotContain(
                    window.GetVisualDescendants().OfType<TextBlock>(),
                    t => t.Text?.StartsWith("Not Found:", StringComparison.Ordinal) == true);
            },
            CancellationToken.None);

    [Fact]
    public Task Closing_it_takes_the_detail_off_the_screen() =>
        headless.Session.Dispatch(
            () =>
            {
                var (window, shell) = OpenDrawer();

                // Found by what they do rather than by name: the scrim and the ✕ are the same command,
                // and this asserts both of them reached the visual tree at all.
                var closers = window.GetVisualDescendants().OfType<Button>()
                    .Where(b => ReferenceEquals(b.Command, shell.CloseDetailCommand))
                    .ToList();

                Assert.Equal(2, closers.Count);

                closers[0].Command!.Execute(null);
                Dispatcher.UIThread.RunJobs();

                Assert.False(shell.IsDetailOpen);
                Assert.Empty(window.GetVisualDescendants().OfType<ClusterNamespaceDetailView>());
            },
            CancellationToken.None);
}
