using Kontena.App.ViewModels;
using Kontena.App.Services;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Engines;

namespace Kontena.App.Tests;

/// <summary>
/// The detail drawer (KON-307): a detail opens over the list instead of replacing it.
/// <para>
/// What these tests are really guarding is that the list survives. A detail used to be a page swap,
/// so the page you were reading was gone and coming back rebuilt it — new scroll position, reloaded
/// rows. The drawer's whole point is that the thing behind it is still the same object.
/// </para>
/// </summary>
public sealed class DetailDrawerTests
{
    private static async Task<MainWindowViewModel> ClusterShellAsync(string page)
    {
        var shell = new MainWindowViewModel();
        Assert.True(await shell.EnterClusterModeAsync(new FakeClusterEngine()));

        shell.NavigateCommand.Execute(page);
        await WaitForRowsAsync(shell);

        return shell;
    }

    /// <summary>The list pages load fire-and-forget, so a test has to wait for the rows to exist.</summary>
    private static async Task WaitForRowsAsync(MainWindowViewModel shell)
    {
        for (var i = 0; i < 100 && shell.CurrentPage is IListPage { HasLoaded: false }; i++)
            await Task.Delay(5);
    }

    private static NodeCardRow FirstNode(MainWindowViewModel shell) =>
        Assert.IsType<ClusterNodesViewModel>(shell.CurrentPage).Items[0];

    [Fact]
    public async Task Opening_a_node_leaves_the_list_where_it_was()
    {
        var shell = await ClusterShellAsync("nodes");
        var list = shell.CurrentPage;

        FirstNode(shell).OpenCommand.Execute(null);

        Assert.IsType<ClusterNodeDetailViewModel>(shell.Detail);
        Assert.True(shell.IsDetailOpen);

        // Same instance, not merely the same type: a rebuilt list has lost the scroll position and
        // the filter, which is exactly what the drawer exists to keep.
        Assert.Same(list, shell.CurrentPage);
    }

    [Fact]
    public async Task Escape_closes_the_drawer()
    {
        var shell = await ClusterShellAsync("nodes");
        FirstNode(shell).OpenCommand.Execute(null);

        // Through the command the key binding uses, including its CanExecute — that guard is the
        // whole of KON-201, and a drawer the binding cannot reach is a drawer Escape does not close.
        Assert.True(shell.DismissCommand.CanExecute(null));
        shell.DismissCommand.Execute(null);

        Assert.Null(shell.Detail);
        Assert.False(shell.IsDetailOpen);
    }

    [Fact]
    public async Task With_no_drawer_and_no_dialog_escape_does_not_match()
    {
        // Avalonia's KeyBinding marks the key handled once the command runs, so a command that can
        // always execute swallows Escape everywhere — including inside a terminal (KON-201).
        var shell = await ClusterShellAsync("nodes");

        Assert.False(shell.DismissCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_dialog_over_the_drawer_takes_the_first_escape()
    {
        var shell = await ClusterShellAsync("nodes");
        FirstNode(shell).OpenCommand.Execute(null);

        // Raised from inside the drawer, which is the case that matters: the drain modal belongs to
        // the shell because it outlives the page it was started from.
        Assert.IsType<ClusterNodeDetailViewModel>(shell.Detail).DrainCommand.Execute(null);
        Assert.True(shell.IsDialogOpen);

        shell.DismissCommand.Execute(null);

        // The question is gone; the thing it was about is not.
        Assert.False(shell.IsDialogOpen);
        Assert.True(shell.IsDetailOpen);

        shell.DismissCommand.Execute(null);

        Assert.False(shell.IsDetailOpen);
    }

    [Fact]
    public async Task Leaving_the_list_takes_the_drawer_with_it()
    {
        var shell = await ClusterShellAsync("nodes");
        FirstNode(shell).OpenCommand.Execute(null);

        shell.NavigateCommand.Execute("namespaces");

        // A drawer belongs to the list it was opened from. Left behind, it would hang over an
        // unrelated page describing something that page does not list.
        Assert.Null(shell.Detail);
    }

    [Fact]
    public async Task Opening_a_second_detail_disposes_the_first()
    {
        var shell = await ClusterShellAsync("namespaces");
        var list = Assert.IsType<ClusterNamespacesViewModel>(shell.CurrentPage);

        list.Items[0].OpenCommand.Execute(null);
        var first = shell.Detail;

        list.Items[1].OpenCommand.Execute(null);

        Assert.NotSame(first, shell.Detail);

        // Nothing observable to assert on a namespace page, which is the point of doing this here
        // rather than with the pages that stream: the ownership rule has to hold before a live log
        // subscription depends on it (KON-309, KON-310).
        Assert.IsType<ClusterNamespaceDetailViewModel>(shell.Detail);
    }

    [Fact]
    public async Task The_drawer_can_hand_its_detail_to_the_full_page()
    {
        var shell = await ClusterShellAsync("nodes");

        FirstNode(shell).OpenCommand.Execute(null);
        var detail = shell.Detail;

        shell.OpenDetailAsPageCommand.Execute(null);

        // The same view model, moved — not rebuilt. A reload here would lose the tab you were on and
        // ask the cluster again for what is already on the screen.
        Assert.Same(detail, shell.CurrentPage);
        Assert.False(shell.IsDetailOpen);

        // And this one *is* a navigation, so Back means the list.
        Assert.Equal("Back to Nodes", shell.BackTooltip);
        shell.GoBackCommand.Execute(null);

        // Rebuilt rather than the same instance, and that is the history's rule, not this one's: a
        // cluster page is a route, because keeping the instance would hand back a page whose streams
        // were disposed on the way out (KON-173).
        Assert.IsType<ClusterNodesViewModel>(shell.CurrentPage);
    }

    [Fact]
    public async Task Handing_it_over_does_not_dispose_it()
    {
        // The drawer disposes what it held on close, which is what makes a second detail safe. The
        // full page is the one caller that must not get that treatment — it is about to show the
        // object it would have thrown away.
        var shell = await ClusterShellAsync("namespaces");
        Assert.IsType<ClusterNamespacesViewModel>(shell.CurrentPage).Items[0].OpenCommand.Execute(null);

        var detail = Assert.IsType<ClusterNamespaceDetailViewModel>(shell.Detail);
        shell.OpenDetailAsPageCommand.Execute(null);

        // Still answering, rather than a disposed object rendering as a blank page.
        Assert.False(string.IsNullOrEmpty(detail.Name));
        Assert.Same(detail, shell.CurrentPage);
    }

    [Fact]
    public void The_dragged_width_is_clamped_and_remembered()
    {
        var path = Path.Combine(Path.GetTempPath(), "kontena-drawer-" + Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(path);
        var shell = new MainWindowViewModel(new BackendRegistry([]), store, store.Load(), new FakeUpdateService());

        var start = shell.DetailWidth;
        shell.ResizeDetail(120);

        Assert.Equal(start + 120, shell.DetailWidth);
        Assert.Equal(start + 120, store.Load().DetailDrawerWidth);

        // Dragged past the edge of the window it would stop being a drawer, and dragged shut it would
        // be a way to lose the panel without closing it.
        shell.ResizeDetail(-5000);
        Assert.True(shell.DetailWidth >= 460);

        shell.ResizeDetail(5000);
        Assert.True(shell.DetailWidth <= 1200);
    }

    [Fact]
    public async Task A_drawer_is_not_somewhere_you_navigated_to()
    {
        var shell = await ClusterShellAsync("nodes");
        var backBefore = shell.BackTooltip;

        FirstNode(shell).OpenCommand.Execute(null);

        // Back still means "the page before this list". If opening the drawer pushed a step, Back
        // would close the overlay once and leave the page the next time — the mistimed-Escape
        // problem, moved to a different key.
        Assert.Equal(backBefore, shell.BackTooltip);
    }
}
