using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.Sdk;
using Kontena.Engines;
using Kontena.Engines.Fakes;
using Kontena.Core.Orchestration.Fakes;

namespace Kontena.App.Tests;

/// <summary>
/// Where you came from (KON-173). There were five Back commands before this and every one jumped to a
/// fixed destination, so each caller had to know where the user had been.
/// </summary>
public sealed class NavigationHistoryTests
{
    private static MainWindowViewModel Shell()
    {
        var path = Path.Combine(Path.GetTempPath(), "kontena-history-" + Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(path);

        return new MainWindowViewModel(new BackendRegistry([]), store, store.Load(), new FakeUpdateService());
    }

    // ── Landing (KON-263) ───────────────────────────────────────────────────

    [Fact]
    public async Task The_page_a_session_lands_on_is_somewhere_you_can_return_to()
    {
        // Landing set CurrentPage directly instead of navigating, so the shell arrived somewhere
        // without recording that it had — and the very first Back of a session had nothing behind it.
        // The second navigation onwards was fine, which is what made it look like a rendering glitch.
        var shell = new MainWindowViewModel();
        await shell.EnterEngineModeAsync(new FakeEngine());

        Assert.False(shell.CanGoBack);   // the landing page itself has nothing behind it

        shell.NavigateCommand.Execute("images");

        Assert.True(shell.CanGoBack);
        Assert.Equal("Back to Containers", shell.BackTooltip);

        shell.GoBackCommand.Execute(null);

        Assert.Same(shell.Containers, shell.CurrentPage);
    }

    [Fact]
    public async Task And_the_same_holds_for_a_cluster()
    {
        // The cluster half built its overview here rather than navigating to it, so it had the
        // identical gap — found by looking rather than by being reported.
        var shell = new MainWindowViewModel();
        Assert.True(await shell.EnterClusterModeAsync(new FakeClusterEngine()));

        Assert.False(shell.CanGoBack);

        shell.NavigateCommand.Execute("pods");

        Assert.True(shell.CanGoBack);
        Assert.Equal("Back to Overview", shell.BackTooltip);

        shell.GoBackCommand.Execute(null);

        Assert.IsType<ClusterOverviewViewModel>(shell.CurrentPage);
    }

    [Fact]
    public void There_is_nothing_to_go_back_to_at_the_start()
    {
        var shell = Shell();

        Assert.False(shell.CanGoBack);
        Assert.Equal("Back", shell.BackTooltip);
    }

    [Fact]
    public void Back_returns_to_the_page_before()
    {
        var shell = Shell();

        shell.ShowAboutCommand.Execute(null);
        Assert.False(shell.CanGoBack);       // first arrival has nothing behind it

        shell.ShowActivityCommand.Execute(null);
        Assert.True(shell.CanGoBack);
        Assert.Equal("Back to About", shell.BackTooltip);

        shell.GoBackCommand.Execute(null);

        Assert.True(shell.IsAboutSelected);
    }

    [Fact]
    public void Going_back_does_not_push_the_page_you_left()
    {
        // Otherwise Back becomes a toggle between the last two pages and the trail behind them is
        // unreachable.
        var shell = Shell();

        shell.ShowAboutCommand.Execute(null);
        shell.ShowActivityCommand.Execute(null);
        shell.GoBackCommand.Execute(null);

        Assert.False(shell.CanGoBack);
    }

    [Fact]
    public void Back_walks_the_whole_trail()
    {
        var shell = Shell();

        shell.ShowAboutCommand.Execute(null);
        shell.ShowActivityCommand.Execute(null);
        shell.ShowAboutCommand.Execute(null);

        shell.GoBackCommand.Execute(null);
        Assert.True(shell.IsActivitySelected);

        shell.GoBackCommand.Execute(null);
        Assert.True(shell.IsAboutSelected);

        Assert.False(shell.CanGoBack);
    }

    [Fact]
    public void Back_at_the_start_of_the_trail_does_nothing()
    {
        var shell = Shell();
        shell.ShowAboutCommand.Execute(null);

        shell.GoBackCommand.Execute(null);
        shell.GoBackCommand.Execute(null);

        Assert.True(shell.IsAboutSelected);
        Assert.False(shell.CanGoBack);
    }

    [Fact]
    public void The_tooltip_names_where_Back_leads()
    {
        // "Back" alone makes you find out by pressing it.
        var shell = Shell();

        shell.ShowActivityCommand.Execute(null);
        shell.ShowAboutCommand.Execute(null);

        Assert.Equal("Back to Activity", shell.BackTooltip);
    }
}
