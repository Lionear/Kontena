using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.Sdk;
using Kontena.Engines;

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
        shell.ShowSettingsCommand.Execute(null);

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

        shell.ShowSettingsCommand.Execute(null);
        shell.ShowAboutCommand.Execute(null);

        Assert.Equal("Back to Settings", shell.BackTooltip);
    }
}
