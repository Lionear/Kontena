using Kontena.Core.Diagnostics;
using Kontena.Sdk.Models;
using Xunit;

namespace Kontena.Core.Tests;

/// <summary>
/// The engine-side diagnosis rules (KON-150). The two that matter most are the ones that refuse to
/// fire: a container the user stopped and a container that finished its job are not faults, and a
/// warning on either teaches people to ignore the block.
/// </summary>
public sealed class ContainerDiagnosisTests
{
    private static ContainerSummary Container(ContainerState state = ContainerState.Exited) => new()
    {
        Id = "c1",
        Name = "api",
        Image = "ghcr.io/lionear/api:1.4.0",
        State = state,
        Backend = "docker",
    };

    private static ContainerInspect Inspect(
        int exitCode = 0, bool oom = false, int restarts = 0,
        RestartPolicy policy = RestartPolicy.No, long? memoryLimit = null) => new()
    {
        Id = "c1",
        Name = "api",
        Image = "ghcr.io/lionear/api:1.4.0",
        ExitCode = exitCode,
        OomKilled = oom,
        RestartCount = restarts,
        RestartPolicy = policy,
        MemoryLimitBytes = memoryLimit,
    };

    [Fact]
    public void A_running_container_is_not_explained()
    {
        Assert.Null(ContainerDiagnosis.Diagnose(Container(ContainerState.Running), Inspect()));
    }

    [Fact]
    public void A_container_that_finished_cleanly_is_not_a_fault()
    {
        // Exit 0 is a job that did what it was for. A warning here would sit on every one-shot
        // container the user ever ran.
        Assert.Null(ContainerDiagnosis.Diagnose(Container(), Inspect()));
    }

    [Fact]
    public void A_container_that_was_stopped_is_not_a_fault()
    {
        // 143 is 128+SIGTERM — the signal a stop sends. Calling that a failure would put a warning on
        // everything the user stopped themselves.
        Assert.Null(ContainerDiagnosis.Diagnose(Container(), Inspect(exitCode: 143)));
    }

    [Fact]
    public void Without_an_inspect_nothing_is_claimed()
    {
        // The exit code and the OOM flag both live in the inspect payload. Without it there is a state
        // and nothing else, and a state is not a diagnosis.
        Assert.Null(ContainerDiagnosis.Diagnose(Container()));
    }

    [Fact]
    public void An_oom_kill_is_reported_from_the_engines_flag_not_from_the_exit_code()
    {
        var oom = ContainerDiagnosis.Diagnose(
            Container(), Inspect(exitCode: 137, oom: true, memoryLimit: 512_000_000));

        // Same exit code, no flag: some other SIGKILL. Reading that as memory pressure sends someone
        // to raise a limit that was never the problem.
        var killed = ContainerDiagnosis.Diagnose(Container(), Inspect(exitCode: 137));

        Assert.Equal("OOMKilled", oom!.Code);
        Assert.Contains("Memory limit: 512 MB", oom.Evidence);
        Assert.Equal("NonZeroExit", killed!.Code);
    }

    [Fact]
    public void The_memory_limit_survives_the_container_stopping()
    {
        // Live stats end when the container does, and the OOM kill is exactly the moment they are gone.
        // The limit therefore has to come from the inspect, which is what this pins.
        var diagnosis = ContainerDiagnosis.Diagnose(Container(), Inspect(exitCode: 137, oom: true), stats: null);

        Assert.Contains("Memory limit: none set", diagnosis!.Evidence);
    }

    [Fact]
    public void A_restart_loop_is_named_once_it_is_a_loop_and_not_a_restart()
    {
        var once = ContainerDiagnosis.Diagnose(
            Container(ContainerState.Running), Inspect(exitCode: 1, restarts: 1, policy: RestartPolicy.Always));
        var looping = ContainerDiagnosis.Diagnose(
            Container(ContainerState.Running), Inspect(exitCode: 1, restarts: 6, policy: RestartPolicy.Always));

        Assert.Null(once);
        Assert.Equal("RestartLoop", looping!.Code);
        Assert.Contains("Restarts: 6", looping.Evidence);
    }

    [Fact]
    public void A_container_with_no_restart_policy_is_not_called_a_loop()
    {
        // Without a policy the engine never restarts it, so a restart count is history rather than a
        // loop in progress.
        Assert.Null(ContainerDiagnosis.Diagnose(
            Container(ContainerState.Running), Inspect(exitCode: 1, restarts: 9)));
    }

    [Fact]
    public void A_missing_command_is_explained_as_a_missing_command()
    {
        var diagnosis = ContainerDiagnosis.Diagnose(Container(), Inspect(exitCode: 127));

        Assert.Equal("NonZeroExit", diagnosis!.Code);
        Assert.Contains("not found in the image", diagnosis.Explanation, StringComparison.Ordinal);
    }
}
