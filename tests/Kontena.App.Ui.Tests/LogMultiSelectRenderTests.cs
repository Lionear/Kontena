using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kontena.App.ViewModels;
using Kontena.App.Views;
using Kontena.Core.Models;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Engines.Fakes;
using Kontena.Sdk.Models;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// Several log lines at once, copied together (KON-463) — the follow-up to KON-452, which made a
/// single line selectable and said so.
/// <para>
/// Driven through real pointer input for the same reason LogSelectionRenderTests is: turning the list
/// to <c>SelectionMode="Multiple"</c> and asserting the property would pass on all three viewers while
/// no click on earth could select a row, because KON-452's transparent <see cref="SelectableTextBlock"/>
/// takes every press before the row sees it. What is worth testing is the gesture.
/// </para>
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class LogMultiSelectRenderTests(HeadlessSessionFixture headless)
{
    private HeadlessUnitTestSession Session => headless.Session;

    /// <summary>
    /// Pod logs, Ctrl-clicked out of order on purpose: what comes back is in the order it is read on
    /// screen, not the order it was clicked.
    /// </summary>
    [Fact]
    public Task Ctrl_clicking_pod_log_rows_copies_them_joined_by_newlines() =>
        Session.Dispatch(async () =>
        {
            var (window, lines, list) = await OpenPodLogsAsync();

            CtrlClick(window, Row(window, lines[2].Message));
            CtrlClick(window, Row(window, lines[0].Message));

            Assert.Equal(2, list.Selection.Count);

            window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, null);

            // And the line itself is the whole line: the timestamp on screen plus the untouched text,
            // level included — a pasted stack trace has to read the way the console does.
            Assert.Equal($"{lines[0].Timestamp} INFO  starting c0 in api-7d9c", lines[0].ForClipboard);
            Assert.Equal(
                $"{lines[0].ForClipboard}\n{lines[2].ForClipboard}", await CopiedAsync(window));
        }, CancellationToken.None).Unwrap();

    /// <summary>
    /// Container logs, and through the right-click menu rather than the keyboard — that menu only
    /// exists because the behaviour takes Fluent's own Copy flyout off the lines first, so a viewer
    /// that missed that step would open a menu that copies nothing.
    /// </summary>
    [Fact]
    public Task Shift_clicking_container_log_rows_copies_the_whole_range() =>
        Session.Dispatch(async () =>
        {
            var (window, lines, list) = await OpenContainerLogsAsync();

            // A plain click leaves the row alone but sets where the range grows from.
            Click(window, Row(window, lines[0].Message), RawInputModifiers.None);
            CtrlClick(window, Row(window, lines[^1].Message), RawInputModifiers.Shift);

            Assert.Equal(lines.Count, list.Selection.Count);

            // Right-clicking a line has to reach the list's menu. The line brings its own Copy flyout
            // from Fluent and keeps it — taking it off with a style does not stick, a control theme's
            // setter holds against it — so what has to be true is that the line never gets the button
            // and the list's menu is the one that opens. That is the quiet failure this pins: a menu
            // that looks right and copies the wrong thing.
            var text = Row(window, lines[0].Message);
            Assert.NotNull(text.ContextFlyout);

            var point = At(text, window, 4);
            window.MouseDown(point, MouseButton.Right);
            window.MouseUp(point, MouseButton.Right);
            Settle(window);
            Assert.True(list.ContextMenu?.IsOpen, "right-clicking a log line opened no menu");
            Assert.False(text.ContextFlyout!.IsOpen, "the line's own flyout opened on top of it");

            InvokeCopyMenu(list);

            Assert.Equal(
                string.Join('\n', lines.Select(l => l.ForClipboard)), await CopiedAsync(window));
        }, CancellationToken.None).Unwrap();

    /// <summary>Compose logs carry their service name into the paste — the column is half the line.</summary>
    [Fact]
    public Task Compose_log_rows_copy_with_the_service_they_came_from() =>
        Session.Dispatch(async () =>
        {
            var (window, page, list) = await OpenComposeLogsAsync();

            CtrlClick(window, Row(window, page.Lines[0].Text));
            CtrlClick(window, Row(window, page.Lines[1].Text));

            InvokeCopyMenu(list);

            Assert.Equal("web  boot", page.Lines[0].ForClipboard);
            Assert.Equal("web  boot\ndb  ready", await CopiedAsync(window));
        }, CancellationToken.None).Unwrap();

    /// <summary>
    /// The two gestures coexist (the point of the ticket's warning): a plain drag still selects text
    /// inside one line, exactly as KON-452 left it, and it drops any rows so a following copy cannot
    /// hand back a selection the user has visibly moved on from.
    /// </summary>
    [Fact]
    public Task A_plain_drag_still_selects_text_inside_one_line_and_clears_the_rows() =>
        Session.Dispatch(async () =>
        {
            var (window, lines, list) = await OpenPodLogsAsync();
            var line = Row(window, lines[1].Message);

            CtrlClick(window, Row(window, lines[0].Message));
            Assert.Equal(1, list.Selection.Count);

            var from = At(line, window, 1);
            var to = At(line, window, line.Bounds.Width - 1);
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(to, RawInputModifiers.LeftMouseButton);
            window.MouseUp(to, MouseButton.Left);
            Settle(window);

            Assert.Equal(lines[1].Message, line.SelectedText);
            Assert.Equal(0, list.Selection.Count);

            // Ctrl-C still belongs to the text selection while one is showing.
            window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, null);
            Assert.Equal(lines[1].Message, await CopiedAsync(window));

            // And so does the right-click Copy, which is the other half of what KON-452 gave a line:
            // with no rows selected the menu falls back to the text inside the line it was opened on.
            var point = At(line, window, 4);
            window.MouseDown(point, MouseButton.Right);
            window.MouseUp(point, MouseButton.Right);
            Settle(window);

            InvokeCopyMenu(list);
            Assert.Equal(lines[1].Message, await CopiedAsync(window));
        }, CancellationToken.None).Unwrap();

    // ── Opening the three viewers ─────────────────────────────────────────────

    private static async Task<(Window Window, IReadOnlyList<LogLineViewModel> Lines, ListBox List)>
        OpenPodLogsAsync()
    {
        var cluster = new FakeClusterEngine();
        var pods = await cluster.ListPodsAsync("app");
        var page = new ClusterPodDetailViewModel(
            cluster, pods.First(p => p.Name == "api-7d9c"), new TerminalFont("JetBrains Mono", 13, false));
        page.SelectTabCommand.Execute("logs");

        var window = Open(new ClusterPodDetailView { DataContext = page });
        await SettleUntilAsync(window, () => page.Lines.Count >= 4);

        return (window, page.Lines.ToList(), ListOf(window, page.Lines));
    }

    private static async Task<(Window Window, IReadOnlyList<LogLineViewModel> Lines, ListBox List)>
        OpenContainerLogsAsync()
    {
        var engine = new FakeEngine();
        var container = (await engine.ListContainersAsync(all: true)).First(c => c.State == ContainerState.Running);
        var page = new ContainerDetailViewModel(engine, container, new TerminalFont("JetBrains Mono", 13, false));
        page.SelectTabCommand.Execute("logs");

        var window = Open(new ContainerDetailView { DataContext = page });
        await SettleUntilAsync(window, () => page.Lines.Count >= 2);

        // A follow stream keeps adding; freeze it so the range asserted is the range clicked.
        var lines = page.Lines.ToList();
        return (window, lines, ListOf(window, page.Lines));
    }

    private static async Task<(Window Window, ComposeLogsViewModel Page, ListBox List)> OpenComposeLogsAsync()
    {
        // No sources: nothing streams, and the two lines below are the whole log. A live stream would
        // race the clicks.
        var page = new ComposeLogsViewModel(new FakeEngine(), "shop", [], () => { });
        page.Lines.Add(new ComposeLogLine("web", Brushes.White, "boot"));
        page.Lines.Add(new ComposeLogLine("db", Brushes.White, "ready"));

        var window = Open(new ComposeLogsView { DataContext = page });
        await SettleUntilAsync(window, () => true);

        return (window, page, ListOf(window, page.Lines));
    }

    private static Window Open(Control content)
    {
        var window = new Window { Width = 1100, Height = 700, Content = content };
        window.Show();
        Settle(window);
        return window;
    }

    private static async Task SettleUntilAsync(Window window, Func<bool> ready)
    {
        for (var i = 0; i < 50 && !ready(); i++)
        {
            await Task.Yield();
            Settle(window);
        }

        Settle(window);
        Assert.True(ready(), "the log never filled up");
    }

    private static ListBox ListOf(Window window, object lines) =>
        window.GetVisualDescendants().OfType<ListBox>().First(l => ReferenceEquals(l.ItemsSource, lines));

    // ── Input ─────────────────────────────────────────────────────────────────

    /// <summary>The drawn line carrying this text.</summary>
    private static SelectableTextBlock Row(Window window, string text) =>
        window.GetVisualDescendants().OfType<SelectableTextBlock>().First(t => t.Text == text);

    private static Point At(Visual line, Window window, double x) =>
        line.TranslatePoint(new Point(x, line.Bounds.Height / 2), window)
        ?? throw new InvalidOperationException("the log line was never laid out");

    private static void CtrlClick(
        Window window, SelectableTextBlock line, RawInputModifiers modifiers = RawInputModifiers.Control) =>
        Click(window, line, modifiers);

    private static void Click(Window window, SelectableTextBlock line, RawInputModifiers modifiers)
    {
        var point = At(line, window, 4);
        window.MouseDown(point, MouseButton.Left, modifiers);
        window.MouseUp(point, MouseButton.Left, modifiers);
        Settle(window);
    }

    /// <summary>The Copy the behaviour put on the list — the one a right-click reaches.</summary>
    private static void InvokeCopyMenu(ListBox list)
    {
        var copy = Assert.IsType<ContextMenu>(list.ContextMenu).Items.OfType<MenuItem>().Single();
        Assert.Equal("Copy", copy.Header);
        copy.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    private static async Task<string?> CopiedAsync(Window window)
    {
        Settle(window);
        var clipboard = window.Clipboard ?? throw new InvalidOperationException("no clipboard");
        var copied = await clipboard.TryGetDataAsync() ?? throw new InvalidOperationException("nothing was copied");
        return await copied.TryGetTextAsync();
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }
}
