using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// An action on a filtered list leaves the filter alone (KON-377).
/// <para>
/// Cluster pages are rebuilt rather than refreshed, and the rebuild goes through the same
/// <c>NavigateCluster</c> a real navigation does — which clears the search box on purpose (KON-164).
/// Reported on Deployments, where a search narrowing the list to one row was undone by clicking
/// Restart or Scale on that row, but the seam is shared: every cluster action that mutates the page
/// it was fired from comes back through <c>ReloadCurrentClusterPage</c>.
/// </para>
/// </summary>
public sealed class SearchSurvivesActionTests
{
    private static async Task<MainWindowViewModel> ClusterShellAsync(string page)
    {
        var shell = new MainWindowViewModel { SearchDebounce = TimeSpan.Zero };
        Assert.True(await shell.EnterClusterModeAsync(new FakeClusterEngine()));

        shell.NavigateCommand.Execute(page);
        await LoadedAsync(shell);

        return shell;
    }

    private static async Task LoadedAsync(MainWindowViewModel shell)
    {
        for (var i = 0; i < 400 && shell.CurrentPage is not IListPage { HasLoaded: true }; i++)
            await Task.Delay(5);

        Assert.IsAssignableFrom<IListPage>(shell.CurrentPage);
    }

    /// <summary>
    /// Wait for the page the action replaced to actually be gone. The rebuild reads the workload kinds
    /// first (KON-200), so straight after a confirm the old page is still up — and being already
    /// loaded, it would satisfy <see cref="LoadedAsync"/> and let the assertions run against it.
    /// </summary>
    private static async Task RebuiltAsync(MainWindowViewModel shell, object previous)
    {
        for (var i = 0; i < 400 && ReferenceEquals(shell.CurrentPage, previous); i++)
            await Task.Delay(5);

        Assert.NotSame(previous, shell.CurrentPage);
        await LoadedAsync(shell);
    }

    /// <summary>The deployments page, filtered down to the single row named "redis".</summary>
    private static async Task<MainWindowViewModel> OneDeploymentAsync()
    {
        var shell = await ClusterShellAsync(WorkloadNavGroups.KeyFor(WorkloadKind.Deployment));

        shell.SearchText = "redis";

        var page = Assert.IsType<ClusterWorkloadsViewModel>(shell.CurrentPage);
        Assert.Equal("redis", Assert.Single(page.Items).Name);

        return shell;
    }

    private static void AssertStillFiltered(MainWindowViewModel shell)
    {
        Assert.Equal("redis", shell.SearchText);

        var page = Assert.IsType<ClusterWorkloadsViewModel>(shell.CurrentPage);
        Assert.Equal("redis", page.SearchText);
        Assert.Equal("redis", Assert.Single(page.Items).Name);
    }

    [Fact]
    public async Task Restarting_the_one_row_a_search_left_keeps_the_search()
    {
        var shell = await OneDeploymentAsync();
        var page = shell.CurrentPage;

        Assert.IsType<ClusterWorkloadsViewModel>(page).Items[0].RestartCommand.Execute(null);
        await Assert.IsType<ConfirmViewModel>(shell.Dialog).ConfirmCommand.ExecuteAsync(null);

        await RebuiltAsync(shell, page);
        AssertStillFiltered(shell);
    }

    [Fact]
    public async Task Scaling_the_one_row_a_search_left_keeps_the_search()
    {
        var shell = await OneDeploymentAsync();
        var page = shell.CurrentPage;

        Assert.IsType<ClusterWorkloadsViewModel>(page).Items[0].ScaleCommand.Execute(null);

        var scale = Assert.IsType<ScaleWorkloadViewModel>(shell.Dialog);
        scale.Replicas = scale.CurrentReplicas + 1;
        await scale.ApplyCommand.ExecuteAsync(null);

        await RebuiltAsync(shell, page);
        AssertStillFiltered(shell);
    }

    [Fact]
    public async Task Deleting_a_pod_from_a_filtered_list_keeps_the_search_too()
    {
        // The same seam, reached from a different page — which is the point of fixing it there rather
        // than in the two handlers the report happened to name.
        var shell = await ClusterShellAsync("pods");
        shell.SearchText = "api";

        var page = shell.CurrentPage;
        var before = Assert.IsType<ClusterPodsViewModel>(page).Items.Count;
        Assert.True(before > 1);

        Assert.IsType<ClusterPodsViewModel>(page).Items[0].DeleteCommand.Execute(null);
        await Assert.IsType<ConfirmViewModel>(shell.Dialog).ConfirmCommand.ExecuteAsync(null);

        await RebuiltAsync(shell, page);

        Assert.Equal("api", shell.SearchText);
        Assert.Equal("api", Assert.IsType<ClusterPodsViewModel>(shell.CurrentPage).SearchText);
        Assert.Equal(before - 1, Assert.IsType<ClusterPodsViewModel>(shell.CurrentPage).Items.Count);
    }

    [Fact]
    public async Task Navigating_somewhere_else_still_clears_it()
    {
        // KON-164's behaviour, which the fix must not widen into: the page a term filtered is gone,
        // and a term restored onto a different one filters a list with nothing to say it was filtered.
        var shell = await OneDeploymentAsync();

        shell.NavigateCommand.Execute("pods");
        await LoadedAsync(shell);

        Assert.Equal(string.Empty, shell.SearchText);
        Assert.Equal(string.Empty, Assert.IsType<ClusterPodsViewModel>(shell.CurrentPage).SearchText);
    }
}
