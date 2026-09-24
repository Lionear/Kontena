using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kontena.App.ViewModels;
using Kontena.App.Views;
using Kontena.Core.Models;
using Kontena.Core.Orchestration.Fakes;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// The pod Overview draws the config section it was given (KON-390).
/// <para>
/// Against the built view rather than the view model, which <c>PodConfigOverviewTests</c> already
/// covers: what only exists here is the eye button and the icon in it. A geometry resource that is
/// not there throws when the template is applied and never at build time, so nothing else would
/// notice until the page was opened by hand.
/// </para>
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class PodConfigSectionRenderTests(HeadlessSessionFixture headless)
{
    private HeadlessUnitTestSession Session => headless.Session;

    [Fact]
    public Task A_secret_row_draws_one_eye_per_key_and_the_image_is_on_the_tab() =>
        Session.Dispatch(async () =>
        {
            var cluster = new FakeClusterEngine();
            var pods = await cluster.ListPodsAsync("app");

            using var page = new ClusterPodDetailViewModel(
                cluster, pods.First(p => p.Name == "api-7d9c"), new TerminalFont("JetBrains Mono", 13, false));

            var window = Show(page);

            Assert.Contains(
                window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text),
                text => text == "ghcr.io/lionear/api:1.8");

            await page.ConfigRows.Single(r => r.Name == "postgres-credentials").ToggleCommand.ExecuteAsync(null);
            Settle();

            // The tooltip is the button's accessible name, so finding it by that is finding what a
            // screen reader would read out. Since KON-416 it names the key rather than reading
            // "Show the value" on every row of the page — which is also why the data context does the
            // scoping here: the Environment variables section above borrows these very rows, and its
            // eyes say "Show the value of ..." too, so a tooltip filter alone would count them in.
            var eyes = window.GetVisualDescendants().OfType<Button>()
                .Where(b => b.DataContext is ConfigKeyRow
                            && b.GetValue(ToolTip.TipProperty) is string tip
                            && tip.StartsWith("Show the value of ", StringComparison.Ordinal))
                .ToList();

            Assert.Equal(
                ["Show the value of password", "Show the value of username"],
                eyes.Select(e => e.GetValue(ToolTip.TipProperty) as string).Order(StringComparer.Ordinal));
            Assert.All(eyes, eye => Assert.NotEmpty(eye.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>()));
        }, CancellationToken.None).Unwrap();

    private static Window Show(ClusterPodDetailViewModel page)
    {
        var window = new Window
        {
            Width = 1000,
            Height = 900,
            Content = new ClusterPodDetailView { DataContext = page },
        };

        window.Show();
        Settle();

        return window;
    }

    /// <summary>
    /// Ticking the render timer draws what is already there; it does not run the dispatcher's own
    /// queue. An ItemsControl that has just been handed its items therefore has no containers yet —
    /// however often you tick — so the key rows of an opened object stayed out of the visual tree and
    /// there was nothing to count. <c>RunJobs</c> is what materialises them.
    /// </summary>
    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();

        for (var i = 0; i < 3; i++)
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }
}
