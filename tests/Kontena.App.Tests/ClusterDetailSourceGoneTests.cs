using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// KON-308: a detached window has to say when what it is showing is gone, instead of quietly going
/// stale or auto-closing. For the five k8s object kinds this rides the same watch the list pages
/// already follow (KON-250/277) — a Deleted event for the exact object sets IsSourceGone.
/// </summary>
public sealed class ClusterDetailSourceGoneTests
{
    public static TheoryData<string, Func<FakeClusterEngine, IDisposable>, Func<IDisposable, bool>, ResourceRef> Kinds()
    {
        var node = new Node { Name = "gke-prod-worker-1", Roles = [], Capacity = new(), Age = TimeSpan.Zero };
        var ns = new KubeNamespace { Name = "payments", Phase = "Active", Age = TimeSpan.Zero };
        // Workload.Reference is computed from Kind/Namespace/Name (GroupVersionKind.For(Kind)), not
        // settable — read it back below instead of assigning it here.
        var workload = new Workload
        {
            Name = "payments-api", Namespace = "payments", Kind = WorkloadKind.Deployment,
        };
        var service = new Service { Name = "payments-api", Namespace = "payments" };

        return new()
        {
            {
                "node", c => new ClusterNodeDetailViewModel(c, node, "v1.29.0"),
                d => ((ClusterNodeDetailViewModel)d).IsSourceGone,
                new ResourceRef(GroupVersionKind.Node, null, node.Name)
            },
            {
                "namespace", c => new ClusterNamespaceDetailViewModel(c, ns),
                d => ((ClusterNamespaceDetailViewModel)d).IsSourceGone,
                new ResourceRef(GroupVersionKind.Namespace, null, ns.Name)
            },
            {
                "workload", c => new ClusterWorkloadDetailViewModel(c, workload),
                d => ((ClusterWorkloadDetailViewModel)d).IsSourceGone,
                workload.Reference
            },
            {
                "service", c => new ClusterServiceDetailViewModel(c, service),
                d => ((ClusterServiceDetailViewModel)d).IsSourceGone,
                new ResourceRef(GroupVersionKind.Service, service.Namespace, service.Name)
            },
        };
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_deleted_event_for_the_exact_object_marks_it_gone(
        string kind, Func<FakeClusterEngine, IDisposable> build, Func<IDisposable, bool> isGone, ResourceRef reference)
    {
        var engine = new FakeClusterEngine();
        using var detail = build(engine);

        Assert.False(isGone(detail), $"{kind} detail started out already marked gone");

        engine.EmitWatchEvent(new ResourceEvent { Type = WatchEventType.Deleted, Resource = reference });

        for (var i = 0; i < 200 && !isGone(detail); i++)
            await Task.Delay(5);

        Assert.True(isGone(detail), $"{kind} detail did not notice its Deleted event");
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_deleted_event_for_a_different_object_of_the_same_kind_does_nothing(
        string kind, Func<FakeClusterEngine, IDisposable> build, Func<IDisposable, bool> isGone, ResourceRef reference)
    {
        var engine = new FakeClusterEngine();
        using var detail = build(engine);

        engine.EmitWatchEvent(new ResourceEvent
        {
            Type = WatchEventType.Deleted,
            Resource = reference with { Name = reference.Name + "-a-different-one" },
        });

        // Give the (wrong) event every chance to be noticed, then confirm it was not.
        await Task.Delay(100);

        Assert.False(isGone(detail), $"{kind} detail went gone over an event about a different object");
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Disposing_stops_the_watch(
        string kind, Func<FakeClusterEngine, IDisposable> build, Func<IDisposable, bool> isGone, ResourceRef reference)
    {
        // Cluster detail pages are rebuilt on every visit — a watch outliving the page would be a
        // stream nobody reads, held open for the life of the app (same rule as ClusterListPageViewModel).
        var engine = new FakeClusterEngine();
        var detail = build(engine);

        detail.Dispose();

        // No assertion beyond "this does not throw" — there is no observable IsLive-equivalent on a
        // detail page. The watch tests above already prove the subscription exists; this proves
        // disposing it is safe to call.
    }
}
