using k8s.Models;
using Kontena.Adapters.Kubernetes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Adapters.Kubernetes.Tests;

/// <summary>
/// Which pods a drain leaves alone, and why (KON-251).
/// <para>
/// These are the decisions a drain makes about someone else's running workload, so each one is
/// pinned against a hand-built pod. A fake cannot prove them: it would only be repeating whatever
/// this file already believes.
/// </para>
/// </summary>
public class NodeDrainRuleTests
{
    private static V1Pod Pod(
        string? ownerKind = "ReplicaSet",
        bool mirror = false,
        bool emptyDir = false,
        string phase = "Running") => new()
    {
        Metadata = new V1ObjectMeta
        {
            Name = "web-5f2a",
            NamespaceProperty = "app",
            Annotations = mirror
                ? new Dictionary<string, string> { ["kubernetes.io/config.mirror"] = "abc123" }
                : null,
            OwnerReferences = ownerKind is null
                ? null
                : [new V1OwnerReference { ApiVersion = "apps/v1", Kind = ownerKind, Name = "web", Uid = "u", Controller = true }],
        },
        Spec = new V1PodSpec
        {
            Containers = [new V1Container { Name = "web" }],
            Volumes = emptyDir
                ? [new V1Volume { Name = "scratch", EmptyDir = new V1EmptyDirVolumeSource() }]
                : null,
        },
        Status = new V1PodStatus { Phase = phase },
    };

    private static readonly DrainOptions Default = new();

    [Fact]
    public void An_ordinary_controlled_pod_is_evicted()
    {
        Assert.Null(NodeMaintenance.SkipReason(Pod(), Default));
    }

    [Fact]
    public void A_static_pod_is_left_alone_because_its_definition_is_not_in_the_cluster()
    {
        // The apiserver accepts the eviction and the kubelet recreates it immediately: the source of
        // truth is a file on that node's disk. Evicting it is theatre.
        var reason = NodeMaintenance.SkipReason(Pod(mirror: true), Default);

        Assert.NotNull(reason);
        Assert.Contains("file on the node", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_daemonset_pod_is_left_alone_by_default_and_evicted_when_asked()
    {
        // Its controller puts it straight back, so the default is the only workable one — but the
        // flag exists, and turning it off has to actually change the answer.
        Assert.NotNull(NodeMaintenance.SkipReason(Pod(ownerKind: "DaemonSet"), Default));

        Assert.Null(NodeMaintenance.SkipReason(
            Pod(ownerKind: "DaemonSet"), new DrainOptions { IgnoreDaemonSets = false }));
    }

    [Fact]
    public void A_pod_with_local_scratch_storage_needs_its_own_yes()
    {
        // emptyDir lives and dies with the pod, so evicting one is not a move but a deletion of
        // whatever was in it.
        var reason = NodeMaintenance.SkipReason(Pod(emptyDir: true), Default);

        Assert.NotNull(reason);
        Assert.Contains("emptyDir", reason, StringComparison.Ordinal);

        Assert.Null(NodeMaintenance.SkipReason(
            Pod(emptyDir: true), new DrainOptions { DeleteEmptyDirData = true }));
    }

    [Fact]
    public void A_pod_nothing_owns_is_not_moved_quietly()
    {
        // Nothing would recreate it anywhere else, so evicting it is deleting it. kubectl does this
        // under --force; doing it silently is the part that would be wrong.
        var reason = NodeMaintenance.SkipReason(Pod(ownerKind: null), Default);

        Assert.NotNull(reason);
        Assert.Contains("nothing would recreate it", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Succeeded")]
    [InlineData("Failed")]
    public void A_pod_that_has_already_finished_is_not_worth_evicting(string phase)
    {
        // It holds no capacity and is going nowhere; counting it as work to move would make every
        // drain of a node that has ever run a Job look incomplete.
        Assert.Equal("already finished", NodeMaintenance.SkipReason(Pod(phase: phase), Default));
    }

    [Fact]
    public void An_owner_that_is_not_the_controller_does_not_count()
    {
        // OwnerReferences can list several; only the one with controller: true is the thing that
        // would recreate the pod. Reading the first would misclassify a pod at random.
        var pod = Pod(ownerKind: null);
        pod.Metadata.OwnerReferences =
        [
            new V1OwnerReference { ApiVersion = "apps/v1", Kind = "ReplicaSet", Name = "web", Uid = "u", Controller = false },
        ];

        var reason = NodeMaintenance.SkipReason(pod, Default);

        Assert.NotNull(reason);
        Assert.Contains("nothing would recreate it", reason, StringComparison.Ordinal);
    }
}
