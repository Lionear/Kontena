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

    /// <summary>
    /// Every object one overview load hands over, with nothing else of the page's still running.
    /// <para>
    /// The engine's running total is the wrong thing to compare between two pages, and KON-402 is why:
    /// the constructor starts a load <i>and</i> seven watches without awaiting either, so that total
    /// says how long a page has been alive rather than what a load costs. On a loaded CI runner it read
    /// 9 against 6 — three loads on the small cluster against two on the big one — for the reason that
    /// looks backwards until you see it: the small cluster is built first, so it is the one alive long
    /// enough for its watch to settle at 400 ms and reload once more. The big cluster was not cheaper,
    /// it was younger.
    /// </para>
    /// <para>
    /// So: stop the watch before that window is up, let the constructor's own load land, and count from
    /// zero across one load nobody is racing. Same shape as <see cref="ClusterRoundTripBudgetTests"/>,
    /// which clears the counter for the same reason.
    /// </para>
    /// </summary>
    private static async Task<int> ObjectsForOneLoadAsync(FakeClusterEngine cluster)
    {
        var page = new ClusterOverviewViewModel(cluster);
        page.Dispose();

        await Task.Yield();
        await Task.Delay(50);

        cluster.Objects.Clear();
        await page.LoadAsync();

        return cluster.Objects.Values.Sum();
    }

    [Fact]
    public async Task The_overview_costs_the_same_on_a_cluster_of_four_and_one_of_sixteen_thousand()
    {
        var small = await ObjectsForOneLoadAsync(new FakeClusterEngine());
        var big = await ObjectsForOneLoadAsync(Big());

        // The node table is the one thing on the page made of objects, and growing the cluster above
        // did not add nodes. Everything else on it is an integer, and an integer costs the same
        // whatever it counts — which is the entire fix.
        Assert.Equal(small, big);

        // Two zeroes would satisfy that and say nothing. The node table is objects and is drawn on
        // both, so a load that read none of them is a broken page passing a budget.
        Assert.NotEqual(0, small);
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
