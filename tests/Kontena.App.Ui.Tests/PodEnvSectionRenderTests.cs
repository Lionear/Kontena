using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Kontena.App.ViewModels;
using Kontena.App.Views;
using Kontena.Core.Models;
using Kontena.Core.Orchestration.Fakes;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// The pod Overview draws the environment section it was given (KON-416).
/// <para>
/// Against the built view rather than the view model, which <c>PodEnvOverviewTests</c> already
/// covers: what only exists here is the eye and the geometry inside it. A missing icon resource
/// throws when the template is applied and never at build time, so nothing else would notice until
/// the page was opened by hand.
/// </para>
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class PodEnvSectionRenderTests(HeadlessSessionFixture headless)
{
    private HeadlessUnitTestSession Session => headless.Session;

    /// <summary>The three variables the fake's api container declares, in the order it declares them.</summary>
    private static readonly string[] Declared = ["LOG_LEVEL", "PGPASSWORD", "POD_IP"];

    [Fact]
    public Task Only_the_secret_backed_variable_draws_an_eye_and_it_says_which_one() =>
        Session.Dispatch(async () =>
        {
            var cluster = new FakeClusterEngine();
            var pods = await cluster.ListPodsAsync("app");

            using var page = new ClusterPodDetailViewModel(
                cluster, pods.First(p => p.Name == "api-7d9c"), new TerminalFont("JetBrains Mono", 13, false));

            var window = Show(page);
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

            Assert.Contains("LOG_LEVEL", texts);
            Assert.Contains("info", texts);
            Assert.Contains("from secret postgres-credentials.password", texts);
            Assert.Contains("from field status.podIP", texts);

            // The tooltip is the button's accessible name, so finding it by that is finding what a
            // screen reader would read out — and it names the variable rather than "the value".
            // Filtered on visibility, because a hidden control is still a visual descendant: every
            // row builds both eyes and both stay in the tree, so counting them all counts six.
            var eyes = window.GetVisualDescendants().OfType<Button>()
                .Where(b => b.IsEffectivelyVisible
                            && b.GetValue(ToolTip.TipProperty) is string tip
                            && tip.StartsWith("Show the value of ", StringComparison.Ordinal))
                .ToList();

            var eye = Assert.Single(eyes);
            Assert.Equal("Show the value of PGPASSWORD", eye.GetValue(ToolTip.TipProperty));
            Assert.NotEmpty(eye.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>());
        }, CancellationToken.None).Unwrap();

    /// <summary>
    /// Every row in the card is the same height until one is opened.
    /// <para>
    /// The reason this is worth a test rather than an eyeball: a binding that walks through the null
    /// <c>Secret</c> of a literal row resolves to unset, and unset means <c>IsVisible</c> falls back
    /// to its default of <b>true</b>. The first build drew an empty value block on every literal row
    /// and only the screenshot showed it — nothing throws, nothing logs, the row is just taller.
    /// </para>
    /// </summary>
    [Fact]
    public Task A_row_without_a_secret_is_no_taller_than_one_with() =>
        Session.Dispatch(async () =>
        {
            var cluster = new FakeClusterEngine();
            var pods = await cluster.ListPodsAsync("app");

            using var page = new ClusterPodDetailViewModel(
                cluster, pods.First(p => p.Name == "api-7d9c"), new TerminalFont("JetBrains Mono", 13, false));

            var window = Show(page);

            var heights = Declared
                .Select(name => window.GetVisualDescendants().OfType<TextBlock>()
                    .Single(t => t.Text == name)
                    .GetVisualAncestors().OfType<Border>().First().Bounds.Height)
                .ToList();

            Assert.All(heights, h => Assert.True(h > 0, "the env rows did not lay out at all"));
            Assert.All(heights, h => Assert.True(h == heights[0], $"rows differ in height: {string.Join(", ", heights)}"));
        }, CancellationToken.None).Unwrap();

    private static Window Show(ClusterPodDetailViewModel page)
    {
        var window = new Window
        {
            Width = 1000,
            Height = 1400,
            Content = new ClusterPodDetailView { DataContext = page },
        };

        window.Show();

        for (var i = 0; i < 3; i++)
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        return window;
    }
}
