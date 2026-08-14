using Kontena.Sdk.Errors;
using Kontena.Sdk.Models;
using Kontena.Sdk.Tooling;
using Kontena.Sdk.Tooling.Fakes;

namespace Kontena.Adapters.Apple.Tests;

/// <summary>
/// The lifecycle side of <see cref="AppleEngine"/>, plus the two kinds of refusal it can give. Every
/// command line asserted here was run against a real <c>container</c> 1.2.2 before it was written down.
/// </summary>
public sealed class AppleEngineLifecycleTests
{
    private static FakeToolRunner Installed() => new FakeToolRunner().Install(AppleTool.Definition);

    private static AppleEngine Engine(IToolRunner runner) =>
        new(new AppleCli(runner), "apple", "Apple container");

    [Fact]
    public async Task StartContainerAsync_runs_start_with_the_id()
    {
        var runner = Installed();

        await Engine(runner).StartContainerAsync("web");

        Assert.Equal(["start", "web"], Assert.Single(runner.Invocations).Arguments);
    }

    [Fact]
    public async Task StopContainerAsync_runs_stop_with_the_id()
    {
        var runner = Installed();

        await Engine(runner).StopContainerAsync("web");

        Assert.Equal(["stop", "web"], Assert.Single(runner.Invocations).Arguments);
    }

    /// <summary>
    /// There is no <c>restart</c> subcommand, so this is stop-then-start — in that order, and only
    /// starting when the stop worked.
    /// </summary>
    [Fact]
    public async Task RestartContainerAsync_stops_then_starts()
    {
        var runner = Installed();

        await Engine(runner).RestartContainerAsync("web");

        Assert.Equal(2, runner.Invocations.Count);
        Assert.Equal(["stop", "web"], runner.Invocations[0].Arguments);
        Assert.Equal(["start", "web"], runner.Invocations[1].Arguments);
    }

    /// <summary>A failed stop must not be followed by a start: half a restart that reports success is
    /// worse than a restart that failed.</summary>
    [Fact]
    public async Task RestartContainerAsync_does_not_start_when_the_stop_failed()
    {
        var runner = Installed().When(
            i => i.Arguments.Contains("stop"),
            exitCode: 1,
            errorOutput: ["Error: internalError: \"failed to stop container\" (cause: \"boom\")"]);

        await Assert.ThrowsAsync<EngineException>(
            async () => await Engine(runner).RestartContainerAsync("web"));

        Assert.Equal(["stop", "web"], Assert.Single(runner.Invocations).Arguments);
    }

    [Theory]
    [InlineData(false, new[] { "delete", "web" })]
    [InlineData(true, new[] { "delete", "--force", "web" })]
    public async Task RemoveContainerAsync_forces_only_when_asked(bool force, string[] expected)
    {
        var runner = Installed();

        await Engine(runner).RemoveContainerAsync("web", force);

        Assert.Equal(expected, Assert.Single(runner.Invocations).Arguments);
    }

    /// <summary>
    /// The runtime genuinely cannot pause — there is no such subcommand — so this is a permanent
    /// refusal, not a stage of KON-31 that has not landed.
    /// </summary>
    [Fact]
    public async Task PauseContainerAsync_refuses_because_the_runtime_has_no_pause()
    {
        var error = await Assert.ThrowsAsync<NotSupportedException>(
            async () => await Engine(Installed()).PauseContainerAsync("web"));

        Assert.Contains("pause", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A capability flag that promises what the adapter cannot do puts a live button in front of an
    /// exception. Every flag reported false here is either impossible for this runtime or not built yet,
    /// and both mean the UI must not offer it.
    /// </summary>
    [Fact]
    public void Capabilities_promise_nothing_that_would_throw()
    {
        var capabilities = Engine(Installed()).Capabilities;

        // What stays false is what the runtime itself lacks — no PR will change either.
        Assert.False(capabilities.SupportsCompose);
        Assert.False(capabilities.SupportsEvents);

        // What CreateContainerAsync refuses, the flag must also deny — otherwise the Run dialog keeps
        // offering a policy this runtime silently drops.
        Assert.False(capabilities.SupportsRestartPolicy);

        // Everything this adapter can do now says so.
        Assert.True(capabilities.SupportsExec);
        Assert.True(capabilities.SupportsStats);
        Assert.True(capabilities.SupportsPrune);
        Assert.True(capabilities.SupportsBuild);
        Assert.True(capabilities.SupportsVolumeBrowse);

        // Containers run in per-container VMs from a user-level service: there is no root daemon.
        Assert.True(capabilities.Rootless);
    }
}
