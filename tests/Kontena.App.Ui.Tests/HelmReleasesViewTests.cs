using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kontena.App.ViewModels;
using Kontena.App.Views;
using Kontena.Core.Orchestration.Fakes;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// The Helm releases page and its detail reach the screen (KON-473): an Uninstall on every row, a Roll
/// back on every revision but the current one, and the Upgrade tab's review-first flow. Presence and
/// bindings only — no pixels, see <see cref="ClusterRowDeleteActionTests"/>.
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class HelmReleasesViewTests(HeadlessSessionFixture headless)
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

    private static List<Button> Buttons(Window window, string text) =>
        [.. window.GetVisualDescendants().OfType<Button>().Where(b => b.Content as string == text && b.IsEffectivelyVisible)];

    [Fact]
    public Task Every_release_row_offers_Uninstall() => headless.Session.Dispatch(
        () =>
        {
            var page = new ClusterHelmReleasesViewModel(new FakeClusterEngine(), null, _ => { }, _ => { });
            page.LoadAsync().GetAwaiter().GetResult();

            var window = Show(new ClusterHelmReleasesView { DataContext = page });

            Assert.Equal(page.Items.Count, Buttons(window, "Uninstall").Count);
            window.Close();
        },
        CancellationToken.None);

    [Fact]
    public Task The_history_tab_offers_Roll_back_on_all_but_the_current_revision() => headless.Session.Dispatch(
        () =>
        {
            var cluster = new FakeClusterEngine();
            var release = cluster.Helm.ListAsync().AsTask().GetAwaiter().GetResult().Single(r => r.Name == "web");
            var detail = new ClusterHelmReleaseDetailViewModel(cluster.Helm, release, _ => { });
            detail.LoadAsync().GetAwaiter().GetResult();
            detail.Tab = "history";

            var window = Show(new ClusterHelmReleaseDetailView { DataContext = detail });

            Assert.Equal(detail.History.Count - 1, Buttons(window, "Roll back").Count);
            Assert.Single(Buttons(window, "Uninstall"));
            window.Close();
        },
        CancellationToken.None);

    [Fact]
    public Task The_upgrade_button_waits_for_a_review() => headless.Session.Dispatch(
        () =>
        {
            var cluster = new FakeClusterEngine();
            var release = cluster.Helm.ListAsync().AsTask().GetAwaiter().GetResult().Single(r => r.Name == "web");
            var detail = new ClusterHelmReleaseDetailViewModel(cluster.Helm, release);
            detail.LoadAsync().GetAwaiter().GetResult();
            detail.Tab = "upgrade";
            detail.UpgradeChart = "ingress-nginx/ingress-nginx";

            var window = Show(new ClusterHelmReleaseDetailView { DataContext = detail });
            var upgrade = Assert.Single(Buttons(window, "Upgrade"), b => b.Classes.Contains("primary"));

            Assert.False(upgrade.IsEffectivelyEnabled);

            detail.ReviewCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.True(upgrade.IsEffectivelyEnabled);
            window.Close();
        },
        CancellationToken.None);
}
