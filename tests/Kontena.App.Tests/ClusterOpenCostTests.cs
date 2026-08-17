using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;

namespace Kontena.App.Tests;

/// <summary>
/// What opening a cluster asks the cluster for (KON-375).
/// <para>
/// The reported symptom was "fetching a Kubernetes cluster feels slow", with no root cause and no
/// profiling behind it. This is the root cause: the shell filled the namespace picker by hand, built
/// the first page, then selected a namespace — and selecting one reads the workload kinds and rebuilds
/// the page, because which page Workloads is depends on those kinds (KON-200). So the landing page was
/// built, thrown away with its watch streams, and built again, and the namespaces were listed five
/// times over.
/// </para>
/// <para>
/// None of it was visible: the fake answers instantly, so every test passed and the cost only showed
/// on a real cluster over a real network, where each of those rounds is a round-trip and they compete
/// with each other for the same connection pool.
/// </para>
/// <para>
/// Asserted as exact numbers rather than "fewer than before". A ceiling drifts upwards one reasonable
/// change at a time, and this is a page whose reads have already crept twice (KON-338, KON-352).
/// </para>
/// </summary>
public sealed class ClusterOpenCostTests
{
    /// <summary>
    /// The open, and what it asked for as that stood the moment it returned.
    /// <para>
    /// A snapshot rather than the live counter (KON-406): the open leaves the namespace watch and the
    /// landing page's seven running, and every watch opens with a burst that <c>ClusterWatch</c>
    /// settles into a reload 400 ms later. Reading the counter at assertion time therefore counts the
    /// open plus however long this test took to be resumed — which on a loaded runner is the
    /// difference between one namespace list and two.
    /// </para>
    /// </summary>
    private static async Task<(MainWindowViewModel Shell, IReadOnlyDictionary<string, int> Reads)> OpenAsync()
    {
        var cluster = new FakeClusterEngine();
        var shell = new MainWindowViewModel();

        Assert.True(await shell.EnterClusterModeAsync(cluster));
        return (shell, new Dictionary<string, int>(cluster.Calls, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Opening_a_cluster_lists_its_namespaces_once_and_no_more()
    {
        var (_, reads) = await OpenAsync();

        // Once, for the picker and the workload kinds together. The overview's copy was the second,
        // and it is gone: the tile it filled wants the number, which it now asks for as a number
        // (KON-395). The listing that is left is the one whose answer is actually read.
        Assert.Equal(1, reads.GetValueOrDefault(nameof(FakeClusterEngine.ListNamespacesAsync)));
    }

    [Fact]
    public async Task Opening_a_cluster_builds_its_landing_page_once()
    {
        var (_, reads) = await OpenAsync();

        // The overview reads the nodes and nothing else does, so its node count is the page count.
        // Two here is the page being built, disposed and built again — seven watch streams opened and
        // torn down for nothing, on top of six reads nobody ever saw the answer to.
        Assert.Equal(1, reads.GetValueOrDefault(nameof(FakeClusterEngine.ListNodesAsync)));
    }

    [Fact]
    public async Task Opening_a_cluster_still_lands_on_the_overview_with_all_namespaces_selected()
    {
        // The guard on the two above: the cheapest possible open is one that does nothing, and the
        // point of the read that is left is that the picker and the sidebar are filled by the time
        // the shell is usable.
        var (shell, _) = await OpenAsync();

        Assert.IsType<ClusterOverviewViewModel>(shell.CurrentPage);
        Assert.Equal("All namespaces", shell.SelectedNamespace);
        Assert.Contains("All namespaces", shell.Namespaces);
        Assert.Contains("app", shell.Namespaces);
        Assert.True(shell.IsReady);
    }
}
