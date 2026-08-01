using Kontena.App.ViewModels;
using Kontena.Core.Models;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>KON-308, the pod half — see ClusterDetailSourceGoneTests for the other five kinds.</summary>
public sealed class ClusterPodDetailSourceGoneTests
{
    private static Pod SamplePod() => new()
    {
        Name = "payments-api-7d4f9-x2k1",
        Namespace = "payments",
        Phase = PodPhase.Running,
        Containers = [new ContainerStatus { Name = "api", Image = "payments-api:1.0", Ready = true }],
    };

    private static TerminalFont Font() => new("JetBrains Mono", 13, true);

    [Fact]
    public async Task A_deleted_event_for_the_exact_pod_marks_it_gone()
    {
        var engine = new FakeClusterEngine();
        var pod = SamplePod();
        using var detail = new ClusterPodDetailViewModel(engine, pod, Font());

        Assert.False(detail.IsSourceGone);

        engine.EmitWatchEvent(new ResourceEvent
        {
            Type = WatchEventType.Deleted,
            Resource = new ResourceRef(GroupVersionKind.Pod, pod.Namespace, pod.Name),
        });

        for (var i = 0; i < 200 && !detail.IsSourceGone; i++)
            await Task.Delay(5);

        Assert.True(detail.IsSourceGone);
    }

    [Fact]
    public async Task A_deleted_event_for_a_different_pod_does_nothing()
    {
        var engine = new FakeClusterEngine();
        var pod = SamplePod();
        using var detail = new ClusterPodDetailViewModel(engine, pod, Font());

        engine.EmitWatchEvent(new ResourceEvent
        {
            Type = WatchEventType.Deleted,
            Resource = new ResourceRef(GroupVersionKind.Pod, pod.Namespace, "some-other-pod"),
        });

        await Task.Delay(100);

        Assert.False(detail.IsSourceGone);
    }
}
