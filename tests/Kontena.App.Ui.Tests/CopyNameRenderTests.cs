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
/// The resource name beside every detail page's title can be copied (KON-481).
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class CopyNameRenderTests(HeadlessSessionFixture headless)
{
    private HeadlessUnitTestSession Session => headless.Session;

    /// <summary>
    /// The pod, because that is the name asked for — and through a real click, since the copy lives in
    /// the button's own <c>OnClick</c> rather than a command a test could call around it.
    /// </summary>
    [Fact]
    public Task Clicking_copy_beside_the_pod_title_puts_the_pod_name_on_the_clipboard() =>
        Session.Dispatch(async () =>
        {
            var cluster = new FakeClusterEngine();
            var pods = await cluster.ListPodsAsync("app");

            using var page = new ClusterPodDetailViewModel(
                cluster, pods.First(p => p.Name == "api-7d9c"), new TerminalFont("JetBrains Mono", 13, false));

            var window = new Window { Width = 1000, Height = 1400, Content = new ClusterPodDetailView { DataContext = page } };
            window.Show();
            Settle(window);

            var clipboard = window.Clipboard ?? throw new InvalidOperationException("no clipboard");
            await clipboard.ClearAsync();

            var copy = window.GetVisualDescendants().OfType<CopyButton>().Single(b => b.Text == "api-7d9c");
            var point = copy.TranslatePoint(new Point(4, copy.Bounds.Height / 2), window)
                        ?? throw new InvalidOperationException("the copy button was never laid out");
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Settle(window);

            var copied = await clipboard.TryGetDataAsync() ?? throw new InvalidOperationException("nothing was copied");
            Assert.Equal("api-7d9c", await copied.TryGetTextAsync());
        }, CancellationToken.None).Unwrap();

    /// <summary>
    /// And the same button on every resource's detail page, so it is not a pods-only feature.
    /// Presence is enough here: the binding is compiled against each page's view model, so a wrong
    /// path is a build error, not a quiet miss.
    /// </summary>
    [Theory]
    [InlineData(typeof(ClusterPodDetailView))]
    [InlineData(typeof(ClusterWorkloadDetailView))]
    [InlineData(typeof(ClusterServiceDetailView))]
    [InlineData(typeof(ClusterIngressDetailView))]
    [InlineData(typeof(ClusterNetworkPolicyDetailView))]
    [InlineData(typeof(ClusterConfigDetailView))]
    [InlineData(typeof(ClusterStorageClassDetailView))]
    [InlineData(typeof(ClusterNamespaceDetailView))]
    [InlineData(typeof(ClusterNodeDetailView))]
    [InlineData(typeof(ClusterHelmReleaseDetailView))]
    [InlineData(typeof(ContainerDetailView))]
    public Task Every_resource_detail_page_has_a_copy_button_by_its_title(Type view) =>
        Session.Dispatch(
            () =>
            {
                // By its tooltip, which is also its accessible name: the Service page carries a second
                // CopyButton for the hostname, and that one must not stand in for this.
                var page = Assert.IsAssignableFrom<UserControl>(Activator.CreateInstance(view));
                Assert.Single(page.GetLogicalDescendants().OfType<CopyButton>(),
                    b => Equals(b.GetValue(ToolTip.TipProperty), "Copy the name"));
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
