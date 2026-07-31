using System.Collections.ObjectModel;
using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// Live list updates (KON-250). Two things carry this feature: rows that know when they are the same
/// row, so a reload is not visible; and a page that never goes quiet about having stopped following
/// the cluster.
/// </summary>
public sealed class LiveListUpdateTests
{
    private static PodRow Pod(string name = "web-5f2a", PodPhase phase = PodPhase.Running, int restarts = 0) =>
        new(new Pod
        {
            Name = name,
            Namespace = "app",
            Phase = phase,
            Restarts = restarts,
            Node = "worker-1",
            Containers = [new ContainerStatus { Name = "web", Image = "nginx", Ready = true }],
        });

    [Fact]
    public void A_row_whose_pod_did_not_change_equals_its_reloaded_twin()
    {
        // This is the whole reason a reload is invisible. Without it ListSync sees every row as new,
        // replaces all of them, and the scroll position goes with the visuals.
        Assert.Equal(Pod(), Pod());
        Assert.Equal(Pod().GetHashCode(), Pod().GetHashCode());
    }

    [Fact]
    public void A_row_whose_pod_did_change_does_not()
    {
        // And the one that changed has to be replaced, or the list would show a stale phase forever.
        Assert.NotEqual(Pod(), Pod(phase: PodPhase.Failed));
        Assert.NotEqual(Pod(), Pod(restarts: 3));
        Assert.NotEqual(Pod(), Pod(name: "web-5f2b"));
    }

    [Fact]
    public void Reloading_an_unchanged_list_touches_nothing()
    {
        // The property the reconcile exists for, stated directly: same data in, same row objects
        // still in place afterwards.
        ObservableCollection<PodRow> shown = [Pod("a"), Pod("b"), Pod("c")];
        var before = shown.ToList();

        ListSync.Apply(shown, [Pod("a"), Pod("b"), Pod("c")]);

        Assert.Equal(before, shown.ToList());
        Assert.Same(before[1], shown[1]);
    }

    [Fact]
    public void Only_the_row_that_changed_is_replaced()
    {
        ObservableCollection<PodRow> shown = [Pod("a"), Pod("b"), Pod("c")];
        var before = shown.ToList();

        ListSync.Apply(shown, [Pod("a"), Pod("b", PodPhase.Failed), Pod("c")]);

        Assert.Same(before[0], shown[0]);
        Assert.NotSame(before[1], shown[1]);
        Assert.Same(before[2], shown[2]);
        Assert.Equal(3, shown.Count);
    }

    // ── Following, and saying when it is not ────────────────────────────────

    [Fact]
    public void A_page_that_follows_the_cluster_says_nothing_and_a_page_that_cannot_says_why()
    {
        // Silence is the right output for the working case and the wrong one for every other.
        var live = new ClusterPodsViewModel(new FakeClusterEngine(), "app");
        Assert.True(live.IsLive);
        Assert.Null(live.LiveNotice);

        // The all-kinds workloads page is five kinds at once and has no single coordinate to follow.
        var mixed = new ClusterWorkloadsViewModel(new FakeClusterEngine(), "app");
        Assert.False(mixed.IsLive);
        Assert.NotNull(mixed.LiveNotice);
        Assert.Contains("several kinds", mixed.LiveNotice, StringComparison.Ordinal);

        // One kind does have one.
        var single = new ClusterWorkloadsViewModel(new FakeClusterEngine(), "app", kind: WorkloadKind.Deployment);
        Assert.True(single.IsLive);
        Assert.Null(single.LiveNotice);
    }

    [Fact]
    public void A_cluster_that_cannot_watch_is_told_apart_from_one_where_nothing_happens()
    {
        var page = new ClusterPodsViewModel(new FakeClusterEngine(watch: false), "app");

        Assert.False(page.IsLive);
        Assert.NotNull(page.LiveNotice);
        Assert.Contains("refresh", page.LiveNotice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Leaving_the_page_stops_following()
    {
        // Cluster pages are rebuilt on every visit, so a watch that outlived its page would hold a
        // stream open for the life of the app with nobody reading it.
        var page = new ClusterPodsViewModel(new FakeClusterEngine(), "app");
        Assert.True(page.IsLive);

        page.Dispose();

        Assert.False(page.IsLive);
    }
}
