using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// KON-450: the Node, Namespace and Service detail pages read their object once, in the constructor,
/// and their watch loop reacted to <c>Deleted</c> alone — so everything the cluster changed while the
/// page was open went unseen. KON-448 built the hook and used it for workloads; this fills in the
/// other three.
/// <para>
/// Most cases drive the page's own <c>RefreshAsync</c> — the method the watch calls when a Modified
/// event for this object arrives — and await it. What is new in KON-450 is what each page does when
/// it is called, and that is worth testing without a stream in the middle.
/// </para>
/// <para>
/// One case below does drive it through the watch, because that the watch calls
/// <c>OnResourceModifiedAsync</c> at all is the line KON-448 fixed and nothing else here would hold
/// it. The rest stay direct: what each page does when it is called is worth testing without a stream
/// in the middle, and four copies of the same delivery would only be four chances to time out.
/// </para>
/// <para>
/// A watch-driven version of all of these was written first and failed about one run in three, which
/// is what KON-451 chased down. It was never the synchronisation context the first diagnosis blamed:
/// the fake's snapshot was a lazy query over its own live lists, so <c>CordonNodeAsync</c> landing
/// while that snapshot still drained bumped the list version under the enumerator, killed the watch
/// with "Collection was modified", and the page — which reads any exception out of its watch as its
/// object being gone — sat waiting for an event that had in fact been delivered. The fake takes its
/// snapshot in one go now.
/// </para>
/// <para>
/// The world is still moved through real paths — <c>CordonNodeAsync</c>, an apply — rather than by
/// reaching into the fake, so a page that is only being told what to think cannot pass.
/// </para>
/// </summary>
public sealed class DetailPageFollowsItsObjectTests
{
    private static async Task ApplyAsync(FakeClusterEngine engine, string yaml)
    {
        await foreach (var _ in engine.ApplyAsync(new ManifestBundle { Yaml = yaml }))
        {
            // Drained for its effect on the fake, not for the progress.
        }
    }

    // ── Service ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_services_endpoints_follow_its_selector()
    {
        // The lead case. The Endpoints tab is not a stored list — it is derived from the selector
        // every time — so a service pointed somewhere else keeps answering with the pods it used to
        // reach, which is a wrong answer rather than an old one.
        var engine = new FakeClusterEngine();
        var api = (await engine.ListServicesAsync("app")).First(s => s.Name == "api");

        using var detail = new ClusterServiceDetailViewModel(engine, api);
        await detail.RefreshPodsAsync();

        Assert.NotEmpty(detail.Pods);
        Assert.All(detail.Pods, row => Assert.StartsWith("api-", row.Name, StringComparison.Ordinal));
        Assert.Equal("app=api", detail.SelectorText);

        await ApplyAsync(engine, """
            apiVersion: v1
            kind: Service
            metadata:
              name: api
              namespace: app
            spec:
              selector:
                app: web
              ports:
                - port: 80
                  targetPort: 8080
            """);

        await detail.RefreshAsync();

        Assert.Equal("app=web", detail.SelectorText);
        Assert.NotEmpty(detail.Pods);
        Assert.All(detail.Pods, row => Assert.StartsWith("web-", row.Name, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_service_that_is_not_in_the_answer_leaves_the_page_as_it_was()
    {
        // The other half of the contract in RefetchAsync: an object missing from the read is one the
        // same watch is about to report Deleted, and blanking the page in the meantime would replace
        // a few seconds of staleness with a screen of em-dashes.
        var engine = new FakeClusterEngine();
        var ghost = new Service
        {
            Name = "not-in-this-cluster", Namespace = "app", Type = ServiceType.ClusterIp,
            ClusterIp = "10.0.0.9", Selector = new Dictionary<string, string> { ["app"] = "ghost" },
        };

        using var detail = new ClusterServiceDetailViewModel(engine, ghost);

        await detail.RefreshAsync();

        Assert.Equal("10.0.0.9", detail.ClusterIpText);
        Assert.Equal("app=ghost", detail.SelectorText);
    }

    // ── Node ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_node_cordoned_from_outside_reaches_the_page()
    {
        // The page's own Cordon button always looked right — it sets the flag itself. This is the
        // other way a node gets cordoned: somebody else, or kubectl, while the page is open.
        var engine = new FakeClusterEngine();

        // Not cordoned already, so the flag flipping is this refresh and not the seed.
        var node = (await engine.ListNodesAsync()).First(n => !n.Unschedulable);

        using var detail = new ClusterNodeDetailViewModel(engine, node, "v1.29.0");

        Assert.False(detail.Cordoned);
        Assert.Contains("Schedulable", detail.CordonState, StringComparison.Ordinal);

        await engine.CordonNodeAsync(node.Name, cordoned: true);
        await detail.RefreshAsync();

        Assert.True(detail.Cordoned);
        Assert.Contains("Cordoned", detail.CordonState, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_modified_event_on_the_watch_reaches_the_page()
    {
        // The one case that goes through the stream rather than calling RefreshAsync by hand: that a
        // Modified event gets as far as OnResourceModifiedAsync is one line in the shared base
        // (KON-448), and it is the line that had no test at all while KON-451 was open.
        var engine = new FakeClusterEngine();
        var node = (await engine.ListNodesAsync()).First(n => !n.Unschedulable);

        using var detail = new ClusterNodeDetailViewModel(engine, node, "v1.29.0");

        Assert.False(detail.Cordoned);

        await engine.CordonNodeAsync(node.Name, cordoned: true);
        engine.EmitWatchEvent(new ResourceEvent
        {
            Type = WatchEventType.Modified,
            Resource = new ResourceRef(GroupVersionKind.Node, null, node.Name),
        });

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!detail.Cordoned && DateTime.UtcNow < deadline)
            await Task.Delay(5);

        // Not IsSourceGone: a watch that died of an exception sets that instead, which is exactly how
        // this test used to fail — silently, as a page waiting forever.
        Assert.False(detail.IsSourceGone, "the watch ended instead of delivering the event");
        Assert.True(detail.Cordoned, "the Modified event never reached the page");
    }

    [Fact]
    public async Task Refreshing_a_node_keeps_the_pod_count_it_already_had()
    {
        // The refresh reads without pod counts on purpose: that flag is a second, cluster-wide read
        // (KON-395), and a Node event says the node changed and not that pods moved. The number
        // therefore has to be carried across — reading it back as the zero the cheap call returns
        // would turn a saving into a wrong figure on screen.
        var engine = new FakeClusterEngine();
        var node = (await engine.ListNodesAsync()).First(n => n.ScheduledPods > 0 && !n.Unschedulable);

        using var detail = new ClusterNodeDetailViewModel(engine, node, "v1.29.0");

        var before = detail.PodCapacity;

        await engine.CordonNodeAsync(node.Name, cordoned: true);
        await detail.RefreshAsync();

        // The refresh really did land — otherwise "the count did not change" is true for the wrong
        // reason, which is the one way this test could pass while saying nothing.
        Assert.True(detail.Cordoned);

        Assert.Equal(before, detail.PodCapacity);
        Assert.DoesNotContain("0 of", detail.PodCapacity, StringComparison.Ordinal);
    }

    // ── Namespace ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_namespaces_own_fields_follow_the_cluster()
    {
        var engine = new FakeClusterEngine();
        var app = (await engine.ListNamespacesAsync()).First(n => n.Name == "app");

        using var detail = new ClusterNamespaceDetailViewModel(engine, app);

        Assert.DoesNotContain("tier=", detail.Labels, StringComparison.Ordinal);

        await ApplyAsync(engine, """
            apiVersion: v1
            kind: Namespace
            metadata:
              name: app
              labels:
                tier: platform
            """);

        await detail.RefreshAsync();

        Assert.Contains("tier=platform", detail.Labels, StringComparison.Ordinal);
    }
}
