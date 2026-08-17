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
/// <para>
/// <b>One read here is not one apiserver call</b>, which is the blind spot KON-396 walked into: a
/// single <see cref="FakeClusterEngine.ListWorkloadsAsync"/> fans out to five lists in the Kubernetes
/// adapter, so the three reads this file used to allow for a navigation were seven calls on the wire.
/// The fan-out per counted read today:
/// </para>
/// <list type="table">
/// <item><term>ListWorkloadsAsync(kind)</term><description>1 list, in full</description></item>
/// <item><term>ListWorkloadsAsync(null)</term><description>5 lists, in full — Deployment, StatefulSet, DaemonSet, Job, CronJob</description></item>
/// <item><term>ListWorkloadKindsAsync</term><description>5 lists, <c>limit=1</c> each (KON-396)</description></item>
/// <item><term>ListNodesAsync</term><description>2 lists — the nodes, plus every pod for the per-node count; 1 without it (KON-395)</description></item>
/// <item><term>CountAsync</term><description>1 list, <c>limit=1</c> and metadata only (KON-395)</description></item>
/// <item><term>everything else</term><description>1 list each</description></item>
/// </list>
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
        nameof(FakeClusterEngine.ListWorkloadKindsAsync),

        // A count is a round-trip like any other — free in the fake, one request on a real cluster —
        // so it belongs on this axis even though the whole point of it is the other one (KON-395).
        // <see cref="ClusterObjectBudgetTests"/> is where its size is held still.
        nameof(FakeClusterEngine.CountAsync),
    ];

    /// <summary>
    /// How long the cluster has to be left alone before the open counts as over.
    /// <para>
    /// Longer than <c>ClusterWatch</c>'s 400 ms settle, and that is the whole point (KON-406). Opening
    /// a cluster starts the namespace watch and the landing page's seven, and a watch opens with a
    /// snapshot of what is already there — a burst, which settles into exactly one reload a moment
    /// after the open returned. Clearing the counter the instant it returned therefore did not clear
    /// the open: whether its leftovers landed before or after the clear was a race with nothing but
    /// runner speed on the other side, and on macOS the picker's re-read landed inside the window and
    /// was billed to a click that did not make it.
    /// </para>
    /// <para>
    /// Same bug as KON-402 one file over, where the count said how long a page had been alive rather
    /// than what a load cost. There the answer was to stop the watch before that window was up; here
    /// the shell owns one of the two watches and keeps it for as long as the cluster is open, so this
    /// waits the window out instead. Reproduced on Linux with nothing but a 350 ms stall between the
    /// open and the clear.
    /// </para>
    /// </summary>
    private static readonly TimeSpan Settled = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long the counted action gets before its reads are called complete. The page's own load and
    /// the sidebar refresh are both started rather than awaited, so the count is only whole once the
    /// loop they were posted to has run them — but they land in about a millisecond against a fake, so
    /// this is the quiet that proves it rather than a wait that hopes.
    /// </summary>
    private static readonly TimeSpan Landed = TimeSpan.FromMilliseconds(50);

    /// <summary>Wait until nothing has read <paramref name="cluster"/> for <paramref name="quiet"/>.</summary>
    private static async Task QuietAsync(FakeClusterEngine cluster, TimeSpan quiet)
    {
        // Bounded: a fake that never stopped being read should fail the budget below, not hang the
        // suite waiting for a standstill that is not coming.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var before = cluster.Calls.Values.Sum();
            await Task.Delay(quiet);

            if (cluster.Calls.Values.Sum() == before)
                return;
        }
    }

    /// <summary>
    /// Open a cluster, forget what that cost, and hand back the engine holding what
    /// <paramref name="action"/> alone asked of it.
    /// </summary>
    private static async Task<FakeClusterEngine> ReadsFromAsync(Action<MainWindowViewModel> action)
    {
        var cluster = new FakeClusterEngine();
        var shell = new MainWindowViewModel();
        Assert.True(await shell.EnterClusterModeAsync(cluster));

        // Count from a standstill, so the number is the action's and only the action's.
        await QuietAsync(cluster, Settled);

        cluster.Calls.Clear();
        action(shell);

        await Task.Yield();
        await QuietAsync(cluster, Landed);

        return cluster;
    }

    /// <summary>What <paramref name="action"/> costs, in the reads counted above.</summary>
    private static async Task<int> ReadsForAsync(Action<MainWindowViewModel> action) =>
        Counted.Sum((await ReadsFromAsync(action)).CallsTo);

    [Fact]
    public async Task Picking_a_namespace_costs_ten_reads()
    {
        // One for the sidebar, which has to be read before the page can be built at all: which page
        // Workloads is depends on the kinds in the namespace you just picked (KON-200) — the
        // namespaces it used to re-read here are followed by a watch now (KON-396). Nine for the
        // overview being rebuilt around the new filter — ten, counting the cluster info this does not
        // count.
        //
        // Seven before, and the overview's share of it is the one number in this file that went up
        // while the page got cheaper: five of its nine are the workload tile counted kind by kind,
        // and one unfiltered ListWorkloadsAsync was already five requests inside the Kubernetes
        // adapter. The cluster sees no more requests than before; what changed is that eight of them
        // now answer with an integer instead of every object of their kind (KON-395). That trade is
        // the whole ticket, and <see cref="ClusterObjectBudgetTests"/> is the axis it shows up on.
        Assert.Equal(10, await ReadsForAsync(shell => shell.SelectedNamespace = "app"));
    }

    [Fact]
    public async Task Opening_a_page_costs_two_reads()
    {
        // One is the page. The other is the sidebar asking which kinds exist, to redraw a submenu
        // that usually has not changed — five limit=1 lists on the wire, where it used to be five
        // whole ones plus a namespace list (KON-396). On a cluster running CronJobs those five
        // included every finished Job in it, for a click on Deployments.
        Assert.Equal(2, await ReadsForAsync(shell => shell.NavigateCommand.Execute("pods")));
    }

    [Fact]
    public async Task Opening_a_page_that_needs_no_cluster_read_costs_only_the_sidebar()
    {
        // Port forwards are held in memory by the shell. A page that asks the cluster for nothing
        // should cost nothing but the sidebar — and if that ever stops being true, it is because
        // something started reading the cluster on a path that has no reason to.
        Assert.Equal(1, await ReadsForAsync(shell => shell.NavigateCommand.Execute("portforwards")));
    }

    [Fact]
    public async Task The_sidebar_never_asks_for_the_workloads_themselves()
    {
        // The point of KON-396, stated as the thing that must not come back: whatever the numbers
        // above end up being, none of those reads may be the app's most expensive one. A navigation
        // that lists workloads is either the page doing it for itself, or this regressing.
        //
        // Through the same helper as the budgets above, and for the reason KON-406 gave it: the
        // namespace assertion is the one the open's own watch would have broken, by re-reading the
        // picker on this side of the clear.
        var cluster = await ReadsFromAsync(shell => shell.NavigateCommand.Execute("portforwards"));

        Assert.Equal(0, cluster.CallsTo(nameof(FakeClusterEngine.ListWorkloadsAsync)));
        Assert.Equal(0, cluster.CallsTo(nameof(FakeClusterEngine.ListNamespacesAsync)));
    }
}
