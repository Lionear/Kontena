using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kontena.App.ViewModels;
using Kontena.App.Views;
using Kontena.Core.Orchestration.Fakes;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// That the Alerts page actually draws its three sections and its empty state (KON-207). The grouping
/// itself is covered in <c>Kontena.App.Tests</c>; what only a rendered page can catch is a section
/// bound to a collection that does not exist, or an empty state that never becomes visible because
/// the capability it is gated on was spelled differently in the axaml.
/// <para>
/// Text and presence only. Headless Avalonia measures with a stub glyph, so nothing here says
/// anything about widths (KON-332).
/// </para>
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class ClusterAlertsRenderTests(HeadlessSessionFixture headless)
{
    private static (ClusterAlertsViewModel Page, ClusterAlertsView View, IReadOnlyList<string> Text) Render(
        FakeClusterEngine cluster)
    {
        var page = new ClusterAlertsViewModel(cluster, onInstallWithHelm: () => { });
        var view = new ClusterAlertsView { DataContext = page };

        var window = new Window { Content = view, Width = 1200, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Only what is actually on screen. Every notice on this page is a Border that is always in
        // the tree and switched by IsVisible, so reading the raw descendants would find the
        // all-clear and the "no Alertmanager" text at the same time — which is the one thing these
        // tests exist to tell apart.
        return (page, view,
        [
            .. view.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.IsEffectivelyVisible)
                .Select(t => t.Text ?? string.Empty),
        ]);
    }

    [Fact]
    public Task The_three_sections_and_their_groups_are_on_the_page() =>
        headless.Session.Dispatch(
            () =>
            {
                var (page, _, text) = Render(new FakeClusterEngine());

                // The fake answers synchronously, so the page has loaded — asserted, because every
                // check below would pass just as happily against a page that read nothing.
                Assert.True(page.HasFiring);

                Assert.Contains("Firing", text);
                Assert.Contains("Pending", text);
                Assert.Contains("Silenced", text);

                // One group per alertname, with the header carrying the sentence and the rows the
                // instances — both halves have to be on screen, not just the header.
                Assert.Contains("KubePodCrashLooping", text);
                Assert.Contains("redis-7d9c4f-x2ktp", text);
                Assert.Contains("worker-5f8b9d-qq4mn", text);

                // The pending group counts against the rule's `for`, which comes from a second read.
                Assert.Contains(text, t => t.Contains("for: 10m", StringComparison.Ordinal));

                // And the silenced one names who and why.
                Assert.Contains(text, t => t.Contains("rick", StringComparison.Ordinal));

                // The word beside every colour: a severity dot on its own is not a status (KON-204).
                Assert.Contains("critical", text);
                Assert.Contains("pending", text);
                Assert.Contains("silenced", text);
            },
            CancellationToken.None);

    [Fact]
    public Task Without_an_Alertmanager_the_page_says_where_it_looked() =>
        headless.Session.Dispatch(
            () =>
            {
                var (page, _, text) = Render(new FakeClusterEngine { HasAlertmanager = false });

                Assert.False(page.HasAlerting);
                Assert.Contains("No Alertmanager answered on this cluster", text);

                // Not the all-clear. Using the friendlier sentence for the worse case is the whole
                // failure this empty state exists to avoid.
                Assert.DoesNotContain(text, t => t.Contains("all-clear", StringComparison.Ordinal));

                // Verbatim out of the discovery code, so it cannot drift from what was searched.
                Assert.Contains(text, t => t.Contains("alertmanager", StringComparison.OrdinalIgnoreCase));
            },
            CancellationToken.None);

    /// <summary>
    /// The loud badge is a style class switched by a binding, so a typo in either name fails
    /// silently: the pill still draws, just in the quiet colours, and nothing anywhere says so.
    /// </summary>
    [Fact]
    public Task The_firing_count_is_the_one_badge_in_the_sidebar_that_shouts() =>
        headless.Session.Dispatch(
            async () =>
            {
                var shell = new MainWindowViewModel();
                Assert.True(await shell.EnterClusterModeAsync(new FakeClusterEngine()));

                var window = new MainWindow { DataContext = shell };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                var pills = window.GetVisualDescendants().OfType<Border>()
                    .Where(b => b.Classes.Contains("navcount") && b.IsEffectivelyVisible)
                    .ToList();

                // Exactly one, and it is the alerts one: every other entry counts inventory and is
                // silent (KON-354).
                var loud = Assert.Single(pills, b => b.Classes.Contains("loud"));
                Assert.Equal("4", Assert.IsType<TextBlock>(loud.Child).Text);
            },
            CancellationToken.None);

    [Fact]
    public Task The_empty_state_offers_the_Helm_hand_off_rather_than_an_install_of_our_own() =>
        headless.Session.Dispatch(
            () =>
            {
                var (page, view, _) = Render(new FakeClusterEngine { HasAlertmanager = false });

                Assert.True(page.CanInstallWithHelm);

                var button = Assert.Single(
                    view.GetVisualDescendants().OfType<Button>(),
                    b => b.Content as string == "Install with Helm");

                // Wired to the command, not merely present: a dead button is the KON-117 failure.
                Assert.NotNull(button.Command);
                Assert.True(button.IsVisible);
            },
            CancellationToken.None);
}
