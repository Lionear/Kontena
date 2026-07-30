using Kontena.App;
using Kontena.App.ViewModels;
using Kontena.Core.Models;
using Kontena.Core.Shell;

namespace Kontena.App.Tests;

/// <summary>
/// Several terminals on one cluster, and the page that shows them (KON-216).
/// <para>
/// None of these open a shell: a tab is created without one, and the shell starts when a view first
/// attaches. That is deliberate in the design and convenient here.
/// </para>
/// </summary>
public sealed class ClusterTerminalsTests
{
    private const string Backend = "kubernetes:kind-test";
    private static readonly TerminalFont Font = new("JetBrains Mono", 13, Ligatures: false);

    private static ClusterShellRequest Request(string? ns = null) =>
        new("kind-test", "kind-test", "kind-test", ns, ["/home/rick/.kube/config"]);

    [Fact]
    public void Terminals_are_numbered_per_cluster_and_carry_their_namespace()
    {
        var terminals = new ClusterTerminals();

        Assert.Equal("Terminal 1", terminals.Add(Backend, Request()).Title);
        Assert.Equal("Terminal 2 · argocd", terminals.Add(Backend, Request("argocd")).Title);

        // A second cluster counts from one: the number is a label within a cluster, not an id.
        Assert.Equal("Terminal 1", terminals.Add("kubernetes:other", Request()).Title);
    }

    [Fact]
    public void A_cluster_only_sees_its_own_terminals()
    {
        var terminals = new ClusterTerminals();
        terminals.Add(Backend, Request());
        terminals.Add(Backend, Request());
        terminals.Add("kubernetes:other", Request());

        Assert.Equal(2, terminals.CountFor(Backend));
        Assert.Equal(1, terminals.CountFor("kubernetes:other"));
    }

    /// <summary>
    /// The number keeps climbing across closes. Handing "Terminal 1" back out would put a name on screen
    /// that shares nothing with the terminal the user remembers by it.
    /// </summary>
    [Fact]
    public async Task A_closed_terminals_number_is_not_reused()
    {
        var terminals = new ClusterTerminals();
        var first = terminals.Add(Backend, Request());

        await terminals.CloseAsync(first);

        Assert.Equal("Terminal 2", terminals.Add(Backend, Request()).Title);
        Assert.Equal(1, terminals.CountFor(Backend));
    }

    /// <summary>Opening the page with nothing on it would be a Terminal page with no terminal.</summary>
    [Fact]
    public void The_page_opens_a_terminal_when_the_cluster_has_none()
    {
        var terminals = new ClusterTerminals();
        var page = Page(terminals);

        Assert.Single(page.Terminals);
        Assert.Same(page.Terminals[0], page.Selected);
        Assert.False(page.HasTabs);
    }

    /// <summary>
    /// The tabs belong to the registry, not to the page: the page is rebuilt on every visit and the
    /// shells are not, so a second visit has to find what the first one opened.
    /// </summary>
    [Fact]
    public void Reopening_the_page_finds_the_terminals_that_were_already_open()
    {
        var terminals = new ClusterTerminals();
        var first = Page(terminals);
        first.NewTerminal();

        var second = Page(terminals);

        Assert.Equal(2, second.Terminals.Count);
        Assert.True(second.HasTabs);
    }

    [Fact]
    public void Reopening_the_page_lands_on_the_tab_that_was_last_looked_at()
    {
        var terminals = new ClusterTerminals();
        var first = Page(terminals);
        first.NewTerminal();
        first.Select(first.Terminals[0]);

        var second = Page(terminals);

        // Same terminal, rebuilt view model: the page is thrown away on navigation, the tabs are not.
        Assert.Same(second.Terminals[0], second.Selected);
    }

    /// <summary>
    /// Closing the third of four tabs should leave you on the third, not at the front. Losing your place
    /// is a small thing that happens every time.
    /// </summary>
    [Fact]
    public async Task Closing_a_tab_lands_on_its_neighbour()
    {
        var terminals = new ClusterTerminals();
        var page = Page(terminals);
        page.NewTerminal();
        page.NewTerminal();

        var middle = page.Terminals[1];
        var last = page.Terminals[2];
        await page.CloseAsync(middle);

        Assert.Same(last, page.Selected);
    }

    [Fact]
    public async Task Closing_the_last_tab_opens_a_fresh_one()
    {
        var terminals = new ClusterTerminals();
        var page = Page(terminals);

        await page.CloseAsync(page.Terminals[0]);

        Assert.Single(page.Terminals);
        Assert.NotNull(page.Selected);
    }

    /// <summary>
    /// A new tab reads the pickers again rather than copying the page's. Opening a terminal after
    /// switching namespace should land in the namespace you switched to.
    /// </summary>
    [Fact]
    public void A_new_terminal_is_opened_on_what_the_pickers_say_now()
    {
        var terminals = new ClusterTerminals();
        var current = "default";
        var page = new ClusterTerminalsViewModel(
            terminals, Backend, () => Request(current), () => Font, () => { });

        current = "argocd";
        page.NewTerminal();

        Assert.Equal("argocd", page.Terminals[^1].Namespace);
    }

    /// <summary>
    /// While a terminal is in its own window the page must hold nothing at all, not a hidden view. A
    /// hidden view is still a view, and it would attach to the session the window is showing — one
    /// viewer at a time is the reason detaching moves rather than mirrors (KON-217).
    /// </summary>
    [Fact]
    public void A_detached_terminal_is_not_drawn_on_the_page()
    {
        var terminals = new ClusterTerminals();
        var page = Page(terminals);

        Assert.NotNull(page.Shown);
        Assert.False(page.IsSelectedDetached);

        page.Selected!.IsDetached = true;

        Assert.Null(page.Shown);
        Assert.True(page.IsSelectedDetached);
    }

    /// <summary>
    /// The window outlives the page, so closing it has to reach a page that is already open — which is
    /// what the terminal's own notification is for.
    /// </summary>
    [Fact]
    public void Closing_the_window_puts_the_terminal_back_on_a_page_that_is_already_open()
    {
        var terminals = new ClusterTerminals();
        var page = Page(terminals);
        page.Selected!.IsDetached = true;

        page.Selected.IsDetached = false;

        Assert.NotNull(page.Shown);
        Assert.False(page.IsSelectedDetached);
    }

    /// <summary>
    /// Closing the tab of a terminal that is off in its own window has to take the window with it, or it
    /// stands there showing a shell that has already been torn down.
    /// </summary>
    [Fact]
    public async Task Closing_a_detached_terminal_tells_its_window_to_go()
    {
        var terminals = new ClusterTerminals();
        var terminal = terminals.Add(Backend, Request());
        terminal.IsDetached = true;

        var told = false;
        terminal.Closed += () => told = true;

        await terminals.CloseAsync(terminal);

        Assert.True(told);
    }

    private static ClusterTerminalsViewModel Page(ClusterTerminals terminals) =>
        new(terminals, Backend, () => Request(), () => Font, () => { });
}
