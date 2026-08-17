using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;

namespace Kontena.App.Tests;

/// <summary>
/// What each cluster action is allowed to cost in <i>objects</i> (KON-395) — the second axis, beside
/// the reads <see cref="ClusterRoundTripBudgetTests"/> counts.
/// <para>
/// Reads per action is the right axis for a remote cluster, where each one is a round-trip. It is the
/// wrong axis for a big one: six reads that each pull four thousand pods and six that pull an integer
/// are the same number, and nothing like the same page. Every perf fix up to KON-375 lowered the
/// count and none lowered the size, so the overview could keep re-reading the whole cluster on every
/// settled watch burst with the whole suite green.
/// </para>
/// <para>
/// The rule these tests encode: <b>what a page costs must not scale with what the cluster holds</b>,
/// unless the page is showing the objects. So they run twice — a seeded cluster and one grown to
/// thousands — and assert the difference, which is the only form of this claim a fake can hold still.
/// </para>
/// </summary>
public sealed class ClusterObjectBudgetTests
{
    /// <summary>Big enough that anything scaling with it is unmistakable, small enough to stay instant.</summary>
    private const int Bulk = 4000;

    private static FakeClusterEngine Big()
    {
        var cluster = new FakeClusterEngine();
        cluster.Grow(pods: Bulk, workloads: Bulk, services: Bulk, namespaces: Bulk);
        return cluster;
    }

    /// <summary>Every object the fake handed over, whichever call asked for it.</summary>
    private static int ObjectsFrom(FakeClusterEngine cluster) => cluster.Objects.Values.Sum();

    [Fact]
    public async Task The_overview_costs_the_same_on_a_cluster_of_four_and_one_of_sixteen_thousand()
    {
        var small = new FakeClusterEngine();
        var big = Big();

        await new ClusterOverviewViewModel(small).LoadAsync();
        await new ClusterOverviewViewModel(big).LoadAsync();

        // The node table is the one thing on the page made of objects, and growing the cluster above
        // did not add nodes. Everything else on it is an integer, and an integer costs the same
        // whatever it counts — which is the entire fix.
        Assert.Equal(ObjectsFrom(small), ObjectsFrom(big));
    }

    [Fact]
    public async Task The_overview_asks_for_no_pods_at_all()
    {
        var cluster = Big();

        await new ClusterOverviewViewModel(cluster).LoadAsync();

        // Twice per load before this: once for the pod tile, once inside ListNodesAsync for a pods
        // column this page does not draw. The tile is a count now, and the node listing is asked not
        // to fill in what nothing here reads.
        Assert.Equal(0, cluster.ObjectsFrom(nameof(FakeClusterEngine.ListPodsAsync)));
        Assert.Equal(0, cluster.CallsTo(nameof(FakeClusterEngine.ListPodsAsync)));
        Assert.False(cluster.NodesAskedWithPodCounts);
    }

    [Fact]
    public async Task The_overview_still_says_what_is_on_the_cluster()
    {
        // The guard on the two above: the cheapest page is one that shows nothing, and these numbers
        // are the page. Grown by a known amount from a known seed, so both halves have to be right.
        var seeded = new FakeClusterEngine();
        var page = new ClusterOverviewViewModel(seeded);
        await page.LoadAsync();

        var pods = page.PodCount;
        var workloads = page.WorkloadCount;
        var services = page.ServiceCount;
        var namespaces = page.NamespaceCount;

        var grown = new FakeClusterEngine();
        grown.Grow(pods: 10, workloads: 10, services: 10, namespaces: 10);
        var second = new ClusterOverviewViewModel(grown);
        await second.LoadAsync();

        Assert.Equal(pods + 10, second.PodCount);
        Assert.Equal(workloads + 10, second.WorkloadCount);
        Assert.Equal(services + 10, second.ServiceCount);
        Assert.Equal(namespaces + 10, second.NamespaceCount);
        Assert.NotEmpty(second.Nodes);
    }
}
