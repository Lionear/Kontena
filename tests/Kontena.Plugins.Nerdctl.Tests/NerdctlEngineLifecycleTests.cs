using Kontena.Sdk.Errors;
using Kontena.Sdk.Tooling;
using Kontena.Sdk.Tooling.Fakes;

namespace Kontena.Plugins.Nerdctl.Tests;

/// <summary>
/// <see cref="NerdctlEngine"/>'s container lifecycle (KON-141 PR 3 task 1) — start, stop, restart,
/// pause, unpause, remove. All six echo back the name or id they were given rather than anything new
/// (Notes/nerdctl-write-formats.md), so success has nothing on stdout worth asserting on; what
/// discriminates a correct implementation from one that never ran is the exact argument list handed to
/// the fake runner and the exception type a failure translates to, not a return value — several of
/// these methods return <c>void</c>, where "did nothing" and "worked" are indistinguishable without
/// checking the invocation.
/// </summary>
public sealed class NerdctlEngineLifecycleTests
{
    private static FakeToolRunner Installed() => new FakeToolRunner().Install(NerdctlTool.Definition);

    private static NerdctlEngine Engine(IToolRunner runner, string @namespace = "k8s.io") =>
        new(new NerdctlCli(runner, @namespace), $"nerdctl:{@namespace}", $"nerdctl ({@namespace})", @namespace);

    // ── StartContainerAsync ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StartContainerAsync_runs_start_with_the_given_id()
    {
        var runner = Installed().When(_ => true, output: ["web"]);

        await Engine(runner).StartContainerAsync("web");

        Assert.Equal(["--namespace", "k8s.io", "start", "web"], runner.Invocations[^1].Arguments);
    }

    [Fact]
    public async Task StartContainerAsync_for_an_unknown_id_throws_ResourceNotFoundException()
    {
        var runner = Installed().When(_ => true, errorOutput: ["1 errors:\nno such container: nope"], exitCode: 1);

        await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => Engine(runner).StartContainerAsync("nope").AsTask());
    }

    // ── StopContainerAsync ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StopContainerAsync_runs_stop_with_the_given_id()
    {
        var runner = Installed().When(_ => true, output: ["web"]);

        await Engine(runner).StopContainerAsync("web");

        Assert.Equal(["--namespace", "k8s.io", "stop", "web"], runner.Invocations[^1].Arguments);
    }

    [Fact]
    public async Task StopContainerAsync_for_an_unknown_id_throws_ResourceNotFoundException()
    {
        var runner = Installed().When(_ => true, errorOutput: ["1 errors:\nno such container: nope"], exitCode: 1);

        await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => Engine(runner).StopContainerAsync("nope").AsTask());
    }

    // ── RestartContainerAsync ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RestartContainerAsync_runs_restart_with_the_given_id()
    {
        var runner = Installed().When(_ => true, output: ["web"]);

        await Engine(runner).RestartContainerAsync("web");

        Assert.Equal(["--namespace", "k8s.io", "restart", "web"], runner.Invocations[^1].Arguments);
    }

    [Fact]
    public async Task RestartContainerAsync_for_an_unknown_id_throws_ResourceNotFoundException()
    {
        var runner = Installed().When(_ => true, errorOutput: ["1 errors:\nno such container: nope"], exitCode: 1);

        await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => Engine(runner).RestartContainerAsync("nope").AsTask());
    }

    // ── PauseContainerAsync ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PauseContainerAsync_runs_pause_with_the_given_id()
    {
        var runner = Installed().When(_ => true, output: ["web"]);

        await Engine(runner).PauseContainerAsync("web");

        Assert.Equal(["--namespace", "k8s.io", "pause", "web"], runner.Invocations[^1].Arguments);
    }

    [Fact]
    public async Task PauseContainerAsync_for_an_unknown_id_throws_ResourceNotFoundException()
    {
        var runner = Installed().When(_ => true, errorOutput: ["1 errors:\nno such container: nope"], exitCode: 1);

        await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => Engine(runner).PauseContainerAsync("nope").AsTask());
    }

    // ── UnpauseContainerAsync ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UnpauseContainerAsync_runs_unpause_with_the_given_id()
    {
        var runner = Installed().When(_ => true, output: ["web"]);

        await Engine(runner).UnpauseContainerAsync("web");

        Assert.Equal(["--namespace", "k8s.io", "unpause", "web"], runner.Invocations[^1].Arguments);
    }

    [Fact]
    public async Task UnpauseContainerAsync_for_an_unknown_id_throws_ResourceNotFoundException()
    {
        var runner = Installed().When(_ => true, errorOutput: ["1 errors:\nno such container: nope"], exitCode: 1);

        await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => Engine(runner).UnpauseContainerAsync("nope").AsTask());
    }

    // ── RemoveContainerAsync ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RemoveContainerAsync_force_false_runs_rm_without_dash_f()
    {
        var runner = Installed().When(_ => true, output: ["web"]);

        await Engine(runner).RemoveContainerAsync("web");

        Assert.Equal(["--namespace", "k8s.io", "rm", "web"], runner.Invocations[^1].Arguments);
    }

    [Fact]
    public async Task RemoveContainerAsync_force_true_adds_dash_f()
    {
        var runner = Installed().When(_ => true, output: ["web"]);

        await Engine(runner).RemoveContainerAsync("web", force: true);

        Assert.Equal(["--namespace", "k8s.io", "rm", "-f", "web"], runner.Invocations[^1].Arguments);
    }

    [Fact]
    public async Task RemoveContainerAsync_for_an_unknown_id_throws_ResourceNotFoundException()
    {
        var runner = Installed().When(_ => true, errorOutput: ["1 errors:\nno such container: nope"], exitCode: 1);

        await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => Engine(runner).RemoveContainerAsync("nope").AsTask());
    }

    [Fact]
    public async Task RemoveContainerAsync_without_force_on_a_running_container_throws_EngineException_not_ResourceNotFoundException()
    {
        // "is in running status..." names a real container nerdctl refuses to touch right now — a
        // conflict over state, not a missing resource. Collapsing it into ResourceNotFoundException
        // would tell the caller the container does not exist, which is false; this must land as the
        // base EngineException instead, distinct from the not-found tests above.
        var runner = Installed().When(_ => true,
            errorOutput: ["container web is in running status. unpause/stop container first or force removal"],
            exitCode: 1);

        var ex = await Assert.ThrowsAsync<EngineException>(
            () => Engine(runner).RemoveContainerAsync("web").AsTask());

        Assert.IsNotType<ResourceNotFoundException>(ex);
        // nerdctl's own words survive into the exception — it is what tells the caller how to fix this
        // (force or stop first), and a generic message would have thrown that information away.
        Assert.Contains("is in running status", ex.Message, StringComparison.Ordinal);
        Assert.Contains("unpause/stop container first or force removal", ex.Message, StringComparison.Ordinal);
    }

    // ── Shared translations ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StartContainerAsync_translates_a_missing_binary_into_the_shared_engine_exception()
    {
        await Assert.ThrowsAsync<EngineUnreachableException>(
            () => Engine(new FakeToolRunner()).StartContainerAsync("web").AsTask());
    }
}
