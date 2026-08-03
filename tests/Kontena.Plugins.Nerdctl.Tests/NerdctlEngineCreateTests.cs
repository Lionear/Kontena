using Kontena.Sdk.Errors;
using Kontena.Sdk.Models;
using Kontena.Sdk.Tooling;
using Kontena.Sdk.Tooling.Fakes;

namespace Kontena.Plugins.Nerdctl.Tests;

/// <summary>
/// <see cref="NerdctlEngine.CreateContainerAsync"/> (KON-141 PR 3 task 2). Unlike the lifecycle commands
/// (see <see cref="NerdctlEngineLifecycleTests"/>), <c>create</c>'s stdout is the answer — the full
/// 64-character id — so what discriminates a correct implementation here is both the argument list built
/// from the request and the id actually read back from stdout, not just the argument list alone.
/// </summary>
public sealed class NerdctlEngineCreateTests
{
    // A dummy id distinct from anything used as an input anywhere below, so a hard-coded return value
    // could not pass a test by coincidence.
    private const string DummyId = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2";

    private static FakeToolRunner Installed() => new FakeToolRunner().Install(NerdctlTool.Definition);

    private static NerdctlEngine Engine(IToolRunner runner, string @namespace = "k8s.io") =>
        new(new NerdctlCli(runner, @namespace), $"nerdctl:{@namespace}", $"nerdctl ({@namespace})", @namespace);

    [Fact]
    public async Task CreateContainerAsync_builds_the_full_argument_list_from_the_request()
    {
        var runner = Installed().When(_ => true, output: [DummyId]);

        var request = new CreateContainerRequest
        {
            Image = "nginx:latest",
            Name = "web",
            Ports = [new PortBinding(8080, 80, "tcp"), new PortBinding(null, 53, "udp")],
            Environment = new Dictionary<string, string> { ["FOO"] = "bar" },
            Volumes = new Dictionary<string, string> { ["data"] = "/var/data" },
            Network = "mynet",
            RestartPolicy = RestartPolicy.Always,
            Start = false,
        };

        var id = await Engine(runner).CreateContainerAsync(request);

        Assert.Equal(
            [
                "--namespace", "k8s.io", "create",
                "--name", "web",
                "-p", "8080:80/tcp",
                "-p", "53/udp",
                "-e", "FOO=bar",
                "-v", "data:/var/data",
                "--network", "mynet",
                "--restart", "always",
                "nginx:latest",
            ],
            runner.Invocations[0].Arguments);
        Assert.Equal(DummyId, id);
        // Start was false: create must be the only call.
        Assert.Single(runner.Invocations);
    }

    [Fact]
    public async Task CreateContainerAsync_reads_the_id_from_stdout_and_trims_it()
    {
        var runner = Installed().When(_ => true, output: [$"  {DummyId}  "]);

        var id = await Engine(runner).CreateContainerAsync(
            new CreateContainerRequest { Image = "nginx", Start = false });

        Assert.Equal(DummyId, id);
    }

    [Fact]
    public async Task CreateContainerAsync_with_RestartPolicy_No_omits_the_restart_flag()
    {
        var runner = Installed().When(_ => true, output: [DummyId]);

        await Engine(runner).CreateContainerAsync(
            new CreateContainerRequest { Image = "nginx", Start = false });

        Assert.Equal(["--namespace", "k8s.io", "create", "nginx"], runner.Invocations[0].Arguments);
    }

    [Fact]
    public async Task CreateContainerAsync_when_Start_is_true_also_starts_the_returned_id()
    {
        var runner = Installed().When(_ => true, output: [DummyId]);

        var id = await Engine(runner).CreateContainerAsync(
            new CreateContainerRequest { Image = "nginx", Start = true });

        Assert.Equal(DummyId, id);
        Assert.Equal(2, runner.Invocations.Count);
        Assert.Equal(["--namespace", "k8s.io", "start", DummyId], runner.Invocations[1].Arguments);
    }

    [Fact]
    public async Task CreateContainerAsync_translates_a_missing_binary_into_the_shared_engine_exception()
    {
        await Assert.ThrowsAsync<EngineUnreachableException>(
            () => Engine(new FakeToolRunner())
                .CreateContainerAsync(new CreateContainerRequest { Image = "nginx", Start = false })
                .AsTask());
    }

    [Fact]
    public async Task CreateContainerAsync_for_a_generic_failure_throws_EngineException_with_nerdctls_message()
    {
        // Nothing in Notes/nerdctl-write-formats.md gives a stable marker for "name already in use" or
        // "image not found" on create, so these fall to the generic translation — but nerdctl's own
        // words must still survive into the exception rather than being replaced with something generic.
        var runner = Installed().When(_ => true,
            errorOutput: ["conflict: container name \"web\" is already in use"], exitCode: 1);

        var ex = await Assert.ThrowsAsync<EngineException>(
            () => Engine(runner)
                .CreateContainerAsync(new CreateContainerRequest { Image = "nginx", Name = "web", Start = false })
                .AsTask());

        Assert.IsNotType<ResourceNotFoundException>(ex);
        Assert.Contains("already in use", ex.Message, StringComparison.Ordinal);
    }
}
