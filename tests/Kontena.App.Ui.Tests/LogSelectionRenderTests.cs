using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kontena.App.ViewModels;
using Kontena.App.Views;
using Kontena.Core.Models;
using Kontena.Core.Orchestration.Fakes;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// Log lines are text you can select and copy (KON-452).
/// <para>
/// Reported as "waarom kan ik in de pods logs geen regels kopieren?": every console in the app drew
/// its lines with <see cref="TextBlock"/>, which has no selection at all — there was nothing to copy,
/// on any of them. The fix is <see cref="SelectableTextBlock"/> in the item templates, so this test
/// drives a real pointer across a real rendered line rather than asserting the control's type: what
/// broke was the interaction, and a type check would pass on a line no drag can reach (behind a
/// ListBoxItem that swallows the press, say).
/// </para>
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class LogSelectionRenderTests(HeadlessSessionFixture headless)
{
    private HeadlessUnitTestSession Session => headless.Session;

    /// <summary>One of the lines the fake engine streams for a running container.</summary>
    private const string Line = "listening on :8080";

    [Fact]
    public Task A_pod_log_line_can_be_selected_by_dragging_across_it() =>
        Session.Dispatch(async () =>
        {
            var (window, _) = await OpenLogsAsync();
            var line = Rendered(window, Line);

            Drag(window, line);

            Assert.Equal(Line, line.SelectedText);
        }, CancellationToken.None).Unwrap();

    /// <summary>
    /// The selection reaches the clipboard, which is the whole point of the report — selecting text
    /// nobody can copy would be the same complaint again.
    /// </summary>
    [Fact]
    public Task The_selected_line_is_what_Ctrl_C_copies() =>
        Session.Dispatch(async () =>
        {
            var (window, _) = await OpenLogsAsync();
            var line = Rendered(window, Line);

            Drag(window, line);
            window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, null);

            var clipboard = window.Clipboard ?? throw new InvalidOperationException("no clipboard");
            var copied = await clipboard.TryGetDataAsync() ?? throw new InvalidOperationException("nothing was copied");
            Assert.Equal(Line, await copied.TryGetTextAsync());
        }, CancellationToken.None).Unwrap();

    /// <summary>
    /// Tailing does not pull the line out from under the pointer mid-drag: a live log would otherwise
    /// scroll the text away between press and release, and the selection would land somewhere else.
    /// </summary>
    [Fact]
    public Task A_line_arriving_mid_drag_does_not_scroll_the_selection_away() =>
        Session.Dispatch(async () =>
        {
            var (window, page) = await OpenLogsAsync();
            var list = window.GetVisualDescendants().OfType<ListBox>()
                .First(l => l.ItemsSource == page.Lines);
            var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            var line = Rendered(window, Line);

            var from = Point(line, window, 1);
            window.MouseDown(from, MouseButton.Left);

            var offset = scroll.Offset.Y;
            for (var i = 0; i < 40; i++)
                page.Lines.Add(page.Lines[^1]);

            Settle(window);
            Assert.Equal(offset, scroll.Offset.Y);

            // And it tails again once the button is up — the pause is for the drag, not for good.
            window.MouseUp(from, MouseButton.Left);
            page.Lines.Add(page.Lines[^1]);
            Settle(window);
            Assert.True(scroll.Offset.Y > offset, "the log stopped following after the drag ended");
        }, CancellationToken.None).Unwrap();

    private static async Task<(Window Window, ClusterPodDetailViewModel Page)> OpenLogsAsync()
    {
        var cluster = new FakeClusterEngine();
        var pods = await cluster.ListPodsAsync("app");

        var page = new ClusterPodDetailViewModel(
            cluster, pods.First(p => p.Name == "api-7d9c"), new TerminalFont("JetBrains Mono", 13, false));
        page.SelectTabCommand.Execute("logs");

        var window = new Window { Width = 1000, Height = 700, Content = new ClusterPodDetailView { DataContext = page } };
        window.Show();

        for (var i = 0; i < 50 && page.Lines.Count < 4; i++)
        {
            await Task.Yield();
            Settle(window);
        }

        Assert.NotEmpty(page.Lines);
        return (window, page);
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>The drawn line with this text — and it has to be a selectable one to be found.</summary>
    private static SelectableTextBlock Rendered(Window window, string text) =>
        window.GetVisualDescendants().OfType<SelectableTextBlock>().First(t => t.Text == text);

    private static Point Point(Visual line, Window window, double x) =>
        line.TranslatePoint(new Point(x, line.Bounds.Height / 2), window)
        ?? throw new InvalidOperationException("the log line was never laid out");

    private static void Drag(Window window, SelectableTextBlock line)
    {
        var from = Point(line, window, 1);
        var to = Point(line, window, line.Bounds.Width - 1);

        // The button has to be named on the move as well: without it the move reads as a hover, and
        // hovering over text selects nothing.
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        window.MouseUp(to, MouseButton.Left);
    }
}
