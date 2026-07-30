using Kontena.Core.Diagnostics;
using Kontena.Sdk.Orchestration.Models;
using Xunit;

namespace Kontena.Core.Orchestration.Tests;

/// <summary>
/// The diagnosis rules (KON-150). Two things are worth more than any single explanation here: that a
/// pod nobody can explain gets no block at all, and that every rule matches on reason codes rather
/// than on the kubelet's wording — which is not API and changes between versions.
/// </summary>
public sealed class PodDiagnosisTests
{
    private static Pod Pod(
        IReadOnlyList<ContainerStatus>? containers = null,
        IReadOnlyList<ContainerStatus>? init = null,
        PodPhase phase = PodPhase.Running,
        string node = "node-1") => new()
    {
        Name = "api-1",
        Namespace = "app",
        Phase = phase,
        Node = node,
        Containers = containers ?? [],
        InitContainers = init ?? [],
    };

    private static ContainerStatus Waiting(string reason, string name = "api", ContainerKind kind = ContainerKind.App) => new()
    {
        Name = name,
        Kind = kind,
        Image = "ghcr.io/lionear/api:1.4.0",
        RunState = ContainerRunState.Waiting,
        Reason = reason,
    };

    private static ClusterEvent Event(string reason, string message, int count = 1) => new()
    {
        Reason = reason,
        Message = message,
        Severity = EventSeverity.Warning,
        InvolvedObject = new ResourceRef(GroupVersionKind.Pod, "app", "api-1"),
        Source = "kubelet",
        Count = count,
        LastSeen = DateTimeOffset.UtcNow.AddMinutes(-2),
    };

    [Fact]
    public void A_healthy_pod_is_not_explained()
    {
        var pod = Pod([new ContainerStatus { Name = "api", Ready = true, RunState = ContainerRunState.Running }]);

        Assert.Null(PodDiagnosis.Diagnose(pod));
    }

    [Fact]
    public void An_unrecognised_failure_gets_no_block_rather_than_a_guess()
    {
        // Waiting on something we have no rule for. Saying "the pod is not running" here would be a
        // longer way of repeating the status column, and it reads as a diagnosis.
        var pod = Pod([Waiting("SomethingNobodyHasSeenBefore")], phase: PodPhase.Pending);

        Assert.Null(PodDiagnosis.Diagnose(pod));
    }

    [Fact]
    public void A_pending_pod_with_nothing_said_about_it_is_not_a_fault()
    {
        // Every pod is briefly Pending with no node. Diagnosing that would put a warning on every
        // healthy pod for its first second.
        var pod = Pod(phase: PodPhase.Pending, node: "");

        Assert.Null(PodDiagnosis.Diagnose(pod));
    }

    [Fact]
    public void An_image_that_cannot_be_pulled_separates_refused_from_missing()
    {
        var pod = Pod([Waiting("ImagePullBackOff")], phase: PodPhase.Pending);

        var denied = PodDiagnosis.Diagnose(pod,
            [Event("Failed", "Error response from daemon: pull access denied for ghcr.io/lionear/api")]);
        var missing = PodDiagnosis.Diagnose(pod,
            [Event("Failed", "Error response from daemon: manifest unknown")]);

        Assert.Equal("ImagePullBackOff", denied!.Code);
        Assert.Contains("credentials", denied.Explanation, StringComparison.Ordinal);
        Assert.Contains("does not exist", missing!.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void The_registry_is_named_even_when_the_image_reference_does_not_write_it_down()
    {
        var implicitHub = Pod([Waiting("ErrImagePull") with { Image = "postgres:16" }], phase: PodPhase.Pending);
        var explicitHost = Pod([Waiting("ErrImagePull")], phase: PodPhase.Pending);

        Assert.Contains("Registry: docker.io", PodDiagnosis.Diagnose(implicitHub)!.Evidence);
        Assert.Contains("Registry: ghcr.io", PodDiagnosis.Diagnose(explicitHost)!.Evidence);
    }

    [Fact]
    public void A_crash_loop_points_at_the_logs_of_the_run_that_ended()
    {
        var pod = Pod([Waiting("CrashLoopBackOff") with { Restarts = 7, LastExitCode = 1 }]);

        var diagnosis = PodDiagnosis.Diagnose(pod, [Event("BackOff", "Back-off restarting failed container", count: 7)]);

        Assert.Equal("CrashLoopBackOff", diagnosis!.Code);
        Assert.Equal(DiagnosisAction.PreviousLogs, diagnosis.Action);
        Assert.Contains("Restarts: 7", diagnosis.Evidence);
        Assert.Contains("Last exit code: 1", diagnosis.Evidence);
    }

    [Fact]
    public void A_crash_loop_that_is_really_an_oom_kill_says_so()
    {
        // The current state is Waiting, so nothing about the kill is visible there. lastState is the
        // only field that carries it, and without this the user reads logs that will never mention it.
        var pod = Pod([
            Waiting("CrashLoopBackOff") with
            {
                Restarts = 4,
                LastExitCode = 137,
                LastTerminationReason = "OOMKilled",
                MemoryLimitBytes = 512_000_000,
            },
        ]);

        var diagnosis = PodDiagnosis.Diagnose(pod);

        Assert.Contains("memory", diagnosis!.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Last exit code: 137 (SIGKILL)", diagnosis.Evidence);
    }

    [Fact]
    public void An_init_container_is_named_as_the_one_holding_the_pod_up()
    {
        var pod = Pod(
            containers: [new ContainerStatus { Name = "api", RunState = ContainerRunState.Waiting, Reason = "PodInitializing" }],
            init: [Waiting("CrashLoopBackOff", "migrate", ContainerKind.Init)],
            phase: PodPhase.Pending);

        var diagnosis = PodDiagnosis.Diagnose(pod);

        Assert.Contains("migrate", diagnosis!.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Exit_code_137_alone_is_not_called_an_oom_kill_when_the_reason_disagrees()
    {
        // 137 is 128+SIGKILL and any kill -9 produces it. With a reason that says otherwise, claiming
        // memory pressure is exactly the confident wrong answer this rule set exists to avoid.
        var pod = Pod([
            new ContainerStatus
            {
                Name = "api",
                RunState = ContainerRunState.Terminated,
                Reason = "Error",
                ExitCode = 137,
            },
        ], phase: PodPhase.Failed);

        Assert.Null(PodDiagnosis.Diagnose(pod));
    }

    [Fact]
    public void An_unschedulable_pod_repeats_the_schedulers_own_message()
    {
        var pod = Pod(phase: PodPhase.Pending, node: "");
        var message = "0/3 nodes are available: 3 Insufficient memory.";

        var diagnosis = PodDiagnosis.Diagnose(pod, [Event("FailedScheduling", message)]);

        Assert.Equal("FailedScheduling", diagnosis!.Code);
        Assert.Contains(message, diagnosis.Evidence);
    }

    [Fact]
    public void A_failing_probe_is_only_reported_while_the_container_is_still_not_ready()
    {
        var probe = Event("Unhealthy", "Readiness probe failed: HTTP probe failed with statuscode: 503");

        var settled = Pod([new ContainerStatus { Name = "api", Ready = true, RunState = ContainerRunState.Running }]);
        var stuck = Pod([new ContainerStatus { Name = "api", Ready = false, RunState = ContainerRunState.Running }]);

        // A probe that failed once on a pod that then went ready is history, not a diagnosis.
        Assert.Null(PodDiagnosis.Diagnose(settled, [probe]));

        var diagnosis = PodDiagnosis.Diagnose(stuck, [probe]);
        Assert.Equal("Unhealthy", diagnosis!.Code);
        Assert.Contains("readiness", diagnosis.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Events_about_other_pods_in_the_namespace_are_ignored()
    {
        // ListEventsAsync is per namespace, so a caller can hand over the neighbour's failures. Reading
        // those as this pod's would explain a pod that is fine with someone else's problem.
        var pod = Pod(phase: PodPhase.Pending, node: "");
        var neighbour = new ClusterEvent
        {
            Reason = "FailedScheduling",
            Message = "0/3 nodes are available",
            InvolvedObject = new ResourceRef(GroupVersionKind.Pod, "app", "worker-9"),
            LastSeen = DateTimeOffset.UtcNow,
        };

        Assert.Null(PodDiagnosis.Diagnose(pod, [neighbour]));
    }

    [Fact]
    public void A_missing_configmap_or_secret_is_explained_as_configuration_not_code()
    {
        var pod = Pod([Waiting("CreateContainerConfigError")], phase: PodPhase.Pending);

        var diagnosis = PodDiagnosis.Diagnose(pod, [Event("Failed", "secret \"api-credentials\" not found")]);

        Assert.Equal("CreateContainerConfigError", diagnosis!.Code);
        Assert.Contains("secret \"api-credentials\" not found", diagnosis.Evidence);
    }
}
