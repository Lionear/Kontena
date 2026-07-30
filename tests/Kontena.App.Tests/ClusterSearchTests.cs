using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// Search in cluster mode (KON-164). Every cluster page used to be a plain <c>ViewModelBase</c>, so
/// the shell's <c>is IListPage</c> check never matched: the box took text and nothing happened.
/// </summary>
public sealed class ClusterSearchTests
{
    private static async Task<T> Loaded<T>(T page) where T : IListPage
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!page.HasLoaded && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(page.HasLoaded, "page never loaded");
        return page;
    }

    [Fact]
    public async Task Every_cluster_list_is_a_searchable_page()
    {
        // The shell forwards search only to an IListPage. Six pages failing that check is what made
        // the box inert, so the check itself is worth pinning.
        var cluster = new FakeClusterEngine();

        Assert.IsAssignableFrom<IListPage>(new ClusterNodesViewModel(cluster));
        Assert.IsAssignableFrom<IListPage>(new ClusterNamespacesViewModel(cluster));
        Assert.IsAssignableFrom<IListPage>(new ClusterWorkloadsViewModel(cluster, null));
        Assert.IsAssignableFrom<IListPage>(new ClusterPodsViewModel(cluster, null));
        Assert.IsAssignableFrom<IListPage>(new ClusterServicesViewModel(cluster, null));

        await Task.CompletedTask;
    }

    [Fact]
    public async Task Typing_filters_the_pods_list()
    {
        var pods = await Loaded(new ClusterPodsViewModel(new FakeClusterEngine(), null));
        var all = pods.Items.Count;

        pods.SearchText = "api";

        Assert.True(pods.Items.Count < all);
        Assert.All(pods.Items, p => Assert.Contains("api", p.Name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Search_is_case_insensitive_and_trimmed()
    {
        var pods = await Loaded(new ClusterPodsViewModel(new FakeClusterEngine(), null));

        pods.SearchText = "  API  ";

        Assert.NotEmpty(pods.Items);
        Assert.All(pods.Items, p => Assert.Contains("api", p.Name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_pod_can_be_found_by_its_node_and_its_status()
    {
        // The two questions a pod list gets asked: what is on this node, and what is broken.
        var byNode = await Loaded(new ClusterPodsViewModel(new FakeClusterEngine(), null));
        byNode.SearchText = "worker-1";
        Assert.NotEmpty(byNode.Items);
        Assert.All(byNode.Items, p => Assert.Contains("worker-1", p.Node, StringComparison.Ordinal));

        var byStatus = await Loaded(new ClusterPodsViewModel(new FakeClusterEngine(), null));
        byStatus.SearchText = "Init:";
        Assert.NotEmpty(byStatus.Items);
    }

    [Fact]
    public async Task Clearing_the_search_brings_everything_back()
    {
        var pods = await Loaded(new ClusterPodsViewModel(new FakeClusterEngine(), null));
        var all = pods.Items.Count;

        pods.SearchText = "api";
        pods.SearchText = string.Empty;

        Assert.Equal(all, pods.Items.Count);
    }

    [Fact]
    public async Task No_matches_is_reported_as_a_search_result_not_an_empty_page()
    {
        // An empty list with no explanation reads as a page that failed to load.
        var pods = await Loaded(new ClusterPodsViewModel(new FakeClusterEngine(), null));

        pods.SearchText = "nothing-is-called-this";

        Assert.Empty(pods.Items);
        Assert.True(pods.HasNoMatches);
        Assert.False(pods.IsEmpty);
        Assert.False(pods.HasItems);
    }

    [Fact]
    public async Task A_reload_keeps_the_active_search_applied()
    {
        // Refreshing under a search used to be the obvious way to silently show everything again.
        var pods = await Loaded(new ClusterPodsViewModel(new FakeClusterEngine(), null));
        pods.SearchText = "api";
        var filtered = pods.Items.Count;

        await pods.LoadAsync();

        Assert.Equal(filtered, pods.Items.Count);
    }

    [Fact]
    public async Task Search_stays_inside_the_namespace_picker()
    {
        // A hit in another namespace would be a row that vanishes on the next refresh, because the
        // page only ever fetches the active namespace.
        var scoped = await Loaded(new ClusterPodsViewModel(new FakeClusterEngine(), "monitoring"));

        scoped.SearchText = "api";

        Assert.Empty(scoped.Items);
        Assert.All(await new FakeClusterEngine().ListPodsAsync("monitoring"),
            p => Assert.Equal("monitoring", p.Namespace));
    }

    [Fact]
    public async Task A_kind_page_says_which_kind_it_searches()
    {
        // The old placeholder named containers, images and volumes on a Kubernetes cluster.
        var deployments = await Loaded(new ClusterWorkloadsViewModel(new FakeClusterEngine(), null, kind: WorkloadKind.Deployment));
        var all = await Loaded(new ClusterWorkloadsViewModel(new FakeClusterEngine(), null));

        Assert.Equal("Search deployments…", deployments.SearchPlaceholder);
        Assert.Equal("Search workloads…", all.SearchPlaceholder);
    }

    [Fact]
    public async Task Pages_that_are_not_lists_turn_the_box_off_rather_than_ignoring_it()
    {
        // The dashboard wants Refresh but is cards, not a list. Accepting text and doing nothing is
        // the dead-control problem in written form.
        var dashboard = await Loaded(new ClusterWorkloadsDashboardViewModel(new FakeClusterEngine(), null));

        Assert.False(dashboard.SupportsSearch);
        Assert.True(((IListPage)new ClusterPodsViewModel(new FakeClusterEngine(), null)).SupportsSearch);
    }
}
