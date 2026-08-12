using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;

namespace Kontena.App.Tests;

/// <summary>
/// What each cluster action is allowed to cost, counted in reads (KON-375c).
/// <para>
/// This is the answer to "how do we notice this creeping back". Not a timer: wall-clock in CI measures
/// the runner's mood, and the fake has no latency to measure in the first place. What a fake can hold
/// still is the <i>number</i> of reads, and on a remote cluster that number is the multiplier on
/// whatever the network costs — six namespace lists on an open (KON-375) was six round-trips competing
/// for one connection pool, which is what "fetching a cluster feels slow" turned out to be.
/// </para>
/// <para>
/// <b>So "too slow" is defined as: more reads per action than the numbers below.</b> They are the
/// measured cost today, asserted exactly rather than as a ceiling — a ceiling drifts upwards one
/// reasonable change at a time, and these pages' reads have crept twice already (KON-338, KON-352).
/// A change that moves a number is not thereby wrong; it has to move the number here too, in the same
/// commit, where a reviewer can see the trade being made.
/// </para>
/// <para>
/// Opening a cluster has its own file (<see cref="ClusterOpenCostTests"/>) because those numbers are
/// the evidence for a fix rather than a standing budget. Only the cluster-wide lists are counted:
/// they are what a big cluster makes expensive, and a fetch of one named object is not the shape of
/// this problem.
/// </para>
/// </summary>
public sealed class ClusterRoundTripBudgetTests
{
    private static readonly string[] Counted =
    [
        nameof(FakeClusterEngine.ListNamespacesAsync),
        nameof(FakeClusterEngine.ListNodesAsync),
        nameof(FakeClusterEngine.ListPodsAsync),
        nameof(FakeClusterEngine.ListServicesAsync),
        nameof(FakeClusterEngine.ListWorkloadsAsync),
    ];

    /// <summary>Open a cluster, forget what that cost, and count what <paramref name="action"/> costs.</summary>
    private static async Task<int> ReadsForAsync(Action<MainWindowViewModel> action)
    {
        var cluster = new FakeClusterEngine();
        var shell = new MainWindowViewModel();
        Assert.True(await shell.EnterClusterModeAsync(cluster));

        cluster.Calls.Clear();
        action(shell);

        // The page's own load and the sidebar refresh are both started rather than awaited, so the
        // count is only complete once the loop they were posted to has run them.
        await Task.Yield();
        await Task.Delay(50);

        return Counted.Sum(cluster.CallsTo);
    }

    [Fact]
    public async Task Picking_a_namespace_costs_seven_reads()
    {
        // Two for the sidebar, which has to be read before the page can be built at all: which page
        // Workloads is depends on the kinds in the namespace you just picked (KON-200). Five for the
        // overview being rebuilt around the new filter — six, counting the cluster info this does not
        // count. Two of those five ask for what the sidebar just read.
        Assert.Equal(7, await ReadsForAsync(shell => shell.SelectedNamespace = "app"));
    }

    [Fact]
    public async Task Opening_a_page_costs_three_reads()
    {
        // One is the page. The other two are the sidebar refreshing itself behind it — a cluster-wide
        // workload list and a namespace list, on every navigation, to redraw a submenu and a picker
        // that usually have not changed. Cheap next to the twelve this replaced (KON-354) and still
        // the largest thing left on this path: see the KON-375 analysis for why it wants a shared
        // read rather than a cache nobody can invalidate.
        Assert.Equal(3, await ReadsForAsync(shell => shell.NavigateCommand.Execute("pods")));
    }

    [Fact]
    public async Task Opening_a_page_that_needs_no_cluster_read_costs_only_the_sidebar()
    {
        // Port forwards are held in memory by the shell. A page that asks the cluster for nothing
        // should cost nothing but the sidebar — and if that ever stops being true, it is because
        // something started reading the cluster on a path that has no reason to.
        Assert.Equal(2, await ReadsForAsync(shell => shell.NavigateCommand.Execute("portforwards")));
    }
}
