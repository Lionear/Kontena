using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kontena.App.ViewModels;
using Kontena.App.Views;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// The whole node card opens the node, not just its name (KON-479) — and the buttons on it still only
/// do what they say. Driven with a real pointer, because the risk is routing: a card that is itself a
/// button, holding buttons of its own.
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class NodeCardClickTests(HeadlessSessionFixture headless)
{
    private static void Settle()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Click(Window window, Visual target)
    {
        var at = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Settle();
    }

    [Fact]
    public Task Clicking_the_card_opens_the_node_and_Drain_only_drains() => headless.Session.Dispatch(
        () =>
        {
            var opened = new List<string>();
            var drained = new List<string>();
            var page = new ClusterNodesViewModel(
                new FakeClusterEngine(), onDrain: drained.Add, onOpenDetail: n => opened.Add(n.Name));
            page.LoadAsync().GetAwaiter().GetResult();

            var window = new Window { Width = 1200, Height = 900, Content = new ClusterNodesView { DataContext = page } };
            window.Show();
            Settle();

            var card = window.GetVisualDescendants().OfType<Button>()
                .First(b => b.Classes.Contains("kindcard") && b.DataContext is NodeCardRow);
            var node = ((NodeCardRow)card.DataContext!).Name;

            // Well away from the name: the Kubelet caption in the card's lower half.
            Click(window, card.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Kubelet"));
            Assert.Equal([node], opened);

            Click(window, card.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Drain"));
            Assert.Equal([node], drained);
            Assert.Equal([node], opened);
        },
        CancellationToken.None);
}
