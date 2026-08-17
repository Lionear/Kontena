using Avalonia.Controls;
using Avalonia.Headless;
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
            // screen reader would read out.
            var eyes = window.GetVisualDescendants().OfType<Button>()
                .Where(b => b.GetValue(ToolTip.TipProperty) as string == "Show the value")
                .ToList();

            Assert.Equal(2, eyes.Count);
            Assert.All(eyes, eye => Assert.NotEmpty(eye.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>()));
        }, CancellationToken.None);

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

    private static void Settle()
    {
        for (var i = 0; i < 3; i++)
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }
}
