using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kontena.App.Controls;
using Kontena.App.ViewModels;
using Kontena.App.Views;
using Kontena.Core.Models;
using Kontena.Core.Orchestration.Fakes;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// A pod's labels can be copied one chip at a time as <c>key=value</c>, or all at once as a selector
/// that pastes straight into <c>kubectl -l</c> (KON-484). Same <see cref="CopyButton"/> as the name
/// beside the title (KON-481), so through a real click for the same reason.
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class CopyLabelsRenderTests(HeadlessSessionFixture headless)
{
    private HeadlessUnitTestSession Session => headless.Session;

    [Theory]
    [InlineData("tier=web", "tier=web")]
    [InlineData("Copy all labels as a selector for kubectl -l", "app=api,tier=web")]
    public Task Clicking_a_label_copy_puts_the_selector_on_the_clipboard(string tipOrChip, string expected) =>
        Session.Dispatch(async () =>
        {
            var cluster = new FakeClusterEngine();
            var pod = (await cluster.ListPodsAsync("app")).First(p => p.Name == "api-7d9c") with
            {
                Labels = new Dictionary<string, string>(StringComparer.Ordinal) { ["tier"] = "web", ["app"] = "api" },
            };

            using var page = new ClusterPodDetailViewModel(cluster, pod, new TerminalFont("JetBrains Mono", 13, false));

            var window = new Window { Width = 1000, Height = 1400, Content = new ClusterPodDetailView { DataContext = page } };
            window.Show();
            Settle(window);

            var clipboard = window.Clipboard ?? throw new InvalidOperationException("no clipboard");
            await clipboard.ClearAsync();

            var copy = window.GetVisualDescendants().OfType<CopyButton>()
                .Single(b => b.Text == tipOrChip || Equals(b.GetValue(ToolTip.TipProperty), tipOrChip));
            var point = copy.TranslatePoint(new Point(4, copy.Bounds.Height / 2), window)
                        ?? throw new InvalidOperationException("the copy button was never laid out");
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Settle(window);

            var copied = await clipboard.TryGetDataAsync() ?? throw new InvalidOperationException("nothing was copied");
            Assert.Equal(expected, await copied.TryGetTextAsync());
        }, CancellationToken.None).Unwrap();

    /// <summary>The other pages that show labels or a selector carry the selector copy too.</summary>
    [Theory]
    [InlineData(typeof(ClusterWorkloadDetailView), 2)]
    [InlineData(typeof(ClusterServiceDetailView), 1)]
    [InlineData(typeof(ClusterNamespaceDetailView), 1)]
    [InlineData(typeof(ClusterNetworkPolicyDetailView), 1)]
    public Task Pages_with_a_selector_have_a_selector_copy(Type view, int count) =>
        Session.Dispatch(
            () =>
            {
                var page = Assert.IsAssignableFrom<UserControl>(Activator.CreateInstance(view));
                Assert.Equal(count, page.GetLogicalDescendants().OfType<CopyButton>()
                    .Count(b => Equals(b.GetValue(ToolTip.TipProperty), "Copy as a selector for kubectl -l")));
            },
            CancellationToken.None);

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }
}
