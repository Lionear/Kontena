using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// Node and namespace detail (KON-197). Both kinds were dead ends in their grids, and both are
/// cluster-scoped — which is the thing the shared detail base was not built for, since it read its
/// pods and events from the object's own namespace.
/// </summary>
public sealed class NodeAndNamespaceDetailTests
{
    private static ClusterNodeDetailViewModel NodeDetail(
        string name = "gke-prod-worker-1", Func<string, bool, Task>? onCordon = null)
    {
        var engine = new FakeClusterEngine();
        var node = engine.ListNodesAsync().AsTask().GetAwaiter().GetResult().Single(n => n.Name == name);

        return new ClusterNodeDetailViewModel(
            engine, node, apiServerVersion: "v1.29.4", onCordon: onCordon ?? ((_, _) => Task.CompletedTask));
    }

    private static async Task<ClusterNamespaceDetailViewModel> NamespaceDetailAsync(string name = "app")
    {
        var engine = new FakeClusterEngine();
        var ns = (await engine.ListNamespacesAsync()).Single(n => n.Name == name);
        var page = new ClusterNamespaceDetailViewModel(engine, ns);

        // The contents load fire-and-forget from the constructor.
        for (var i = 0; i < 50 && page.ContentsLoading; i++)
            await Task.Delay(5);

        return page;
    }

    [Fact]
    public async Task A_nodes_pods_come_from_every_namespace_and_not_just_one()
    {
        // This is the case the shared base got wrong for free: it read ListPodsAsync(Namespace), and
        // a Node has no namespace — so the page would have listed nothing at all.
        var page = NodeDetail();
        page.SelectedTab = "pods";

        for (var i = 0; i < 50 && page.PodsLoading; i++)
            await Task.Delay(5);

        Assert.NotEmpty(page.Pods);
        Assert.All(page.Pods, row => Assert.Equal("gke-prod-worker-1", row.Node));
    }

    [Fact]
    public void Every_condition_is_listed_and_not_only_the_failing_ones()
    {
        // The card shows the problems because a card has room for what is wrong. This page is where
        // you read what the kubelet actually says, and a healthy MemoryPressure is a fact.
        var page = NodeDetail("gke-prod-worker-2");

        Assert.True(page.Conditions.Count > 1);
        Assert.Contains(page.Conditions, c => c.State == "True");
        Assert.Contains(page.Conditions, c => c.State == "False");
    }

    [Fact]
    public async Task Cordoning_from_the_detail_page_changes_what_the_button_says()
    {
        var asked = new List<(string Node, bool Cordoned)>();
        var page = NodeDetail(onCordon: (name, cordoned) =>
        {
            asked.Add((name, cordoned));
            return Task.CompletedTask;
        });

        Assert.Equal("Cordon", page.CordonLabel);

        await page.ToggleCordonCommand.ExecuteAsync(null);

        Assert.Equal(("gke-prod-worker-1", true), asked.Single());
        Assert.Equal("Uncordon", page.CordonLabel);
        Assert.Contains("Cordoned", page.CordonState, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_node_that_is_cordoned_says_why_nothing_will_arrive()
    {
        // "Nothing is running here" on a cordoned node is true and leaves out the half that matters.
        var page = NodeDetail("gke-prod-cp-1");

        for (var i = 0; i < 50 && page.PodsLoading; i++)
            await Task.Delay(5);

        Assert.True(page.Cordoned);
        Assert.Empty(page.Pods);
        Assert.Contains("nothing new will arrive", page.PodsEmptyReason, StringComparison.Ordinal);
    }

    // ── Namespaces ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_namespace_says_what_is_in_it_by_kind()
    {
        // The list answered "does it exist". This answers the question you had.
        var page = await NamespaceDetailAsync();

        Assert.Contains(page.Contents, c => c.Label == "Workloads" && c.Count > 0);
        Assert.Contains(page.Contents, c => c.Label == "Pods" && c.Count > 0);
        Assert.Contains(page.Contents, c => c.Label == "Services");
        Assert.Contains(page.Contents, c => c.Label == "Ingresses");
        Assert.Contains(page.Contents, c => c.Label == "Volume claims");
    }

    [Fact]
    public async Task A_kind_with_nothing_in_it_is_not_a_link()
    {
        // A zero that clicks through to an empty list is a promise the row already answered.
        var page = await NamespaceDetailAsync("kube-system");

        Assert.All(page.Contents.Where(c => c.Count == 0), c => Assert.False(c.CanOpen));
    }

    [Fact]
    public async Task An_empty_namespace_is_said_outright()
    {
        // It is the answer to "can I delete this", and a column of zeroes makes you count them.
        var page = await NamespaceDetailAsync("kube-system");

        Assert.True(page.IsEmptyNamespace);
    }

    [Fact]
    public async Task A_namespace_reads_its_pods_from_itself()
    {
        var page = await NamespaceDetailAsync();
        page.SelectedTab = "pods";

        for (var i = 0; i < 50 && page.PodsLoading; i++)
            await Task.Delay(5);

        Assert.NotEmpty(page.Pods);
        Assert.All(page.Pods, row => Assert.Equal("app", row.Namespace));
    }

    [Fact]
    public async Task Terminating_is_flagged_and_active_is_not()
    {
        Assert.False((await NamespaceDetailAsync()).IsTerminating);

        var terminating = new ClusterNamespaceDetailViewModel(
            new FakeClusterEngine(),
            new KubeNamespace { Name = "doomed", Phase = "Terminating" });

        Assert.True(terminating.IsTerminating);
        Assert.Contains("finalizer", terminating.TerminatingNote, StringComparison.Ordinal);
    }
}
