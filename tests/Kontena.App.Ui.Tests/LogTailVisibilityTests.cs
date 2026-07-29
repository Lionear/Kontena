using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kontena.App.Behaviors;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// Logs behind a tab open on their last line (KON-198).
/// <para>
/// KON-165 gave every log surface one tail-following behaviour, and this list had it, yet pod logs
/// still opened at the top. The rule was right; the moment was wrong. A tab here is a panel toggled
/// with <c>IsVisible</c>, so the list is attached from the start and only laid out when the tab is
/// picked — which is after every line has already arrived.
/// </para>
/// <para>
/// Driven through a real (headless) layout rather than through the pure rule, because the pure rule
/// was never what broke: <c>LogTailTests</c> passed the whole time. What this pins down is <i>when</i>
/// the behaviour is asked, which is the part the previous fix got wrong.
/// </para>
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class LogTailVisibilityTests(HeadlessSessionFixture headless)
{
    private HeadlessUnitTestSession Session => headless.Session;

    /// <summary>The shape of ClusterPodDetailView: a log panel in the tree from the start, hidden.</summary>
    private sealed record Fixture(Window Window, ListBox List, Grid Tab, ObservableCollection<string> Lines);

    private static Fixture Build(bool follow = true)
    {
        var list = new ListBox();
        AutoScroll.SetEnabled(list, true);
        AutoScroll.SetFollow(list, follow);

        var tab = new Grid { IsVisible = false };
        tab.Children.Add(list);

        var window = new Window { Width = 400, Height = 300, Content = tab };
        window.Show();

        var lines = new ObservableCollection<string>();
        list.ItemsSource = lines;

        // Every line arrives while the tab is hidden — the reported order. Scrolling here does
        // nothing: there is no viewport to scroll in yet.
        for (var i = 0; i < 200; i++)
            lines.Add($"line {i}");

        Settle();
        return new Fixture(window, list, tab, lines);
    }

    /// <summary>
    /// Let layout and the queued dispatcher work settle. The tail-follow itself is posted at
    /// background priority, deliberately (it has to run after the new line is laid out), so one pass
    /// is never enough.
    /// </summary>
    private static void Settle()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static ScrollViewer Scroll(ListBox list) =>
        list.GetVisualDescendants().OfType<ScrollViewer>().First();

    [Fact]
    public Task A_hidden_log_list_opens_on_its_last_line_when_its_tab_is_picked() =>
        Session.Dispatch(
            () =>
            {
                var fixture = Build();

                fixture.Tab.IsVisible = true;
                Settle();

                var scroll = Scroll(fixture.List);
                Assert.True(
                    LogTail.IsAtBottom(scroll.Offset.Y, scroll.Extent.Height, scroll.Viewport.Height),
                    $"offset {scroll.Offset.Y} of extent {scroll.Extent.Height}, viewport {scroll.Viewport.Height}");
            },
            CancellationToken.None);

    [Fact]
    public Task A_tab_picked_with_follow_switched_off_stays_where_it_was() =>
        Session.Dispatch(
            () =>
            {
                // Follow off means someone is reading something further up. Appearing is not a reason
                // to move, the same way a new line is not.
                var fixture = Build(follow: false);

                fixture.Tab.IsVisible = true;
                Settle();

                Assert.Equal(0, Scroll(fixture.List).Offset.Y);
            },
            CancellationToken.None);
}
