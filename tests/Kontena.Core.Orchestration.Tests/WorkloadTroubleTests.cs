using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;
using Xunit;
using Kontena.Core.Orchestration;

namespace Kontena.Core.Orchestration.Tests;

/// <summary>
/// Why a workload is not running as intended (KON-174). The whole point of the WHY column is that it
/// says something the status column cannot, so these pin that it never falls back to restating
/// "Degraded" in longer words while a pod could have told you where to look.
/// </summary>
public sealed class WorkloadTroubleTests
{
    private static Workload Workload(
        RolloutStatus status = RolloutStatus.Degraded, int desired = 1, WorkloadKind kind = WorkloadKind.Deployment) => new()
    {
        Name = "api",
        Namespace = "app",
        Kind = kind,
        Desired = desired,
        RolloutStatus = status,
    };

    private static Pod Pod(
        IReadOnlyList<ContainerStatus>? containers = null, IReadOnlyList<ContainerStatus>? init = null) => new()
    {
        Name = "api-1",
        Namespace = "app",
        Containers = containers ?? [],
        InitContainers = init ?? [],
    };

    private static ContainerStatus Waiting(string name, string reason, ContainerKind kind = ContainerKind.App) => new()
    {
        Name = name,
        Kind = kind,
        RunState = ContainerRunState.Waiting,
        Reason = reason,
    };

    [Fact]
    public void Only_degraded_workloads_are_called_out()
    {
        // Progressing is a rollout doing its job. Putting it on an attention list trains people to
        // ignore the list during every deploy, which is when they most need to read it.
        Assert.True(WorkloadTrouble.NeedsAttention(Workload(RolloutStatus.Degraded)));
        Assert.False(WorkloadTrouble.NeedsAttention(Workload(RolloutStatus.Progressing)));
        Assert.False(WorkloadTrouble.NeedsAttention(Workload(RolloutStatus.Complete)));
        Assert.False(WorkloadTrouble.NeedsAttention(Workload(RolloutStatus.Paused)));
    }

    [Fact]
    public void A_failing_container_is_named_by_its_reason()
    {
        var why = WorkloadTrouble.Describe(Workload(), [Pod([Waiting("api", "CrashLoopBackOff")])]);

        Assert.Equal("Pod in CrashLoopBackOff", why);
    }

    [Fact]
    public void An_init_container_wins_over_an_app_container()
    {
        // A pod held on its init container reads as Pending and its app containers never started, so
        // the app container's "PodInitializing" describes a consequence rather than the cause.
        var pod = Pod(
            containers: [Waiting("api", "PodInitializing")],
            init: [Waiting("run-migrations", "CrashLoopBackOff", ContainerKind.Init)]);

        var why = WorkloadTrouble.Describe(Workload(), [pod]);

        Assert.Contains("run-migrations", why, StringComparison.Ordinal);
        Assert.Contains("exits and restarts", why, StringComparison.Ordinal);
    }

    [Fact]
    public void An_init_container_that_is_merely_slow_is_not_reported_as_looping()
    {
        // Only reasons that mean "keeps trying and keeps failing" count. A one-off wait is not one.
        var pod = Pod(init: [Waiting("wait-for-db", "PodInitializing", ContainerKind.Init)]);

        Assert.DoesNotContain("wait-for-db", WorkloadTrouble.Describe(Workload(), [pod]), StringComparison.Ordinal);
    }

    [Fact]
    public void An_image_that_will_not_pull_is_said_in_words()
    {
        var pod = Pod(init: [Waiting("fetch", "ImagePullBackOff", ContainerKind.Init)]);

        Assert.Contains("image cannot be pulled", WorkloadTrouble.Describe(Workload(), [pod]), StringComparison.Ordinal);
    }

    [Fact]
    public void With_several_pods_the_count_is_given()
    {
        var pods = new[]
        {
            Pod([Waiting("api", "CrashLoopBackOff")]),
            Pod([Waiting("api", "CrashLoopBackOff")]),
            Pod([new ContainerStatus { Name = "api", RunState = ContainerRunState.Running }]),
        };

        Assert.Equal("2 of 3 pods in CrashLoopBackOff", WorkloadTrouble.Describe(Workload(desired: 3), pods));
    }

    [Fact]
    public void No_pods_at_all_says_so_and_says_how_many_were_wanted()
    {
        // The distinction that matters: pods that exist and fail is a different problem from pods that
        // were never created — a quota, an unschedulable node, an admission webhook.
        Assert.Equal("No pods running — 3 wanted", WorkloadTrouble.Describe(Workload(desired: 3), []));
    }

    [Fact]
    public void Pods_that_are_up_but_not_ready_are_distinguished_from_pods_that_are_failing()
    {
        // Running containers with a failing readiness probe report no waiting reason at all, so
        // without this the answer would fall through to something vaguer than the truth.
        var pod = Pod([new ContainerStatus { Name = "api", RunState = ContainerRunState.Running, Ready = false }]);

        Assert.Equal("1 of 1 pods not ready", WorkloadTrouble.Describe(Workload(), [pod]));
    }

    // ── One pod at a time (KON-415) ──────────────────────────────────────────

    [Fact]
    public void A_healthy_pod_has_nothing_to_say()
    {
        var pod = Pod([Running("api", ready: true)]) with { Phase = PodPhase.Running };

        Assert.Null(WorkloadTrouble.DescribePod(pod));
    }

    [Fact]
    public void A_crash_looping_container_is_named_even_though_the_pod_reads_as_Running()
    {
        // This is the whole ticket: the phase says Running, so the status column is the one place
        // that does not mention the problem.
        var pod = Pod([Waiting("api", "CrashLoopBackOff")]) with { Phase = PodPhase.Running };

        Assert.Equal("Pod in CrashLoopBackOff", WorkloadTrouble.DescribePod(pod));
    }

    [Fact]
    public void A_wedged_init_container_is_named_before_the_app_containers_it_is_blocking()
    {
        var pod = Pod(
            [Waiting("api", "PodInitializing")],
            [Waiting("run-migrations", "ImagePullBackOff", ContainerKind.Init)]) with { Phase = PodPhase.Pending };

        // Both halves matter and neither is anywhere else on the row: which container, and why.
        Assert.Equal("Init container run-migrations in ImagePullBackOff", WorkloadTrouble.DescribePod(pod));
    }

    [Fact]
    public void A_container_that_is_merely_starting_is_not_trouble()
    {
        // ContainerCreating and PodInitializing are the kubelet saying "not yet". Marking those would
        // put a red row on every pod that starts, and a warning that is always on is not a warning.
        var pod = Pod([Waiting("api", "ContainerCreating")]) with { Phase = PodPhase.Pending };

        Assert.Null(WorkloadTrouble.DescribePod(pod));
    }

    [Fact]
    public void A_pod_that_keeps_restarting_is_trouble_even_while_it_is_up()
    {
        // Between two backoffs a crash loop looks like a healthy Running pod. The count is the only
        // thing still saying otherwise.
        var pod = Pod([Running("api", ready: true)]) with { Phase = PodPhase.Running, Restarts = 9 };

        Assert.Equal("Restarted 9 times", WorkloadTrouble.DescribePod(pod));
    }

    [Fact]
    public void One_restart_is_not_trouble()
    {
        var pod = Pod([Running("api", ready: true)]) with { Phase = PodPhase.Running, Restarts = 1 };

        Assert.Null(WorkloadTrouble.DescribePod(pod));
    }

    [Fact]
    public void A_running_pod_whose_containers_are_not_ready_is_trouble()
    {
        var pod = Pod([Running("api", ready: true), Running("sidecar", ready: false)])
            with { Phase = PodPhase.Running };

        Assert.Equal("1 of 2 containers not ready", WorkloadTrouble.DescribePod(pod));
    }

    [Fact]
    public void A_pending_pod_whose_containers_are_not_ready_yet_is_not_trouble()
    {
        // Nothing is ready while a pod is being scheduled either. Only Running makes "not ready" mean
        // something went wrong rather than "it has not got there yet".
        var pod = Pod([Running("api", ready: false)]) with { Phase = PodPhase.Pending };

        Assert.Null(WorkloadTrouble.DescribePod(pod));
    }

    [Fact]
    public void A_failed_pod_says_so()
    {
        var pod = Pod([Running("api", ready: false)]) with { Phase = PodPhase.Failed };

        Assert.Equal("Pod failed", WorkloadTrouble.DescribePod(pod));
    }

    private static ContainerStatus Running(string name, bool ready) => new()
    {
        Name = name,
        Ready = ready,
        RunState = ContainerRunState.Running,
    };
}
