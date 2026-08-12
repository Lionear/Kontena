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
            Mounts = [new MountSpec(MountSpec.Volume, "data", "/var/data")],
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

    /// <summary>
    /// A container carries a command, a workdir, a user and labels, and none of them reached the CLI
    /// before KON-350 — a migrated container would have run the image's default CMD instead of its
    /// own. <c>--entrypoint</c> takes one string here and cannot be repeated, so the remaining parts
    /// move to the front of the command, the same shape the Apple adapter uses.
    /// </summary>
    [Fact]
    public async Task CreateContainerAsync_passes_command_workdir_user_and_labels()
    {
        var runner = Installed().When(_ => true, output: [DummyId]);

        await Engine(runner).CreateContainerAsync(new CreateContainerRequest
        {
            Image = "nginx:alpine",
            Entrypoint = ["/docker-entrypoint.sh"],
            Command = ["nginx", "-g", "daemon off;"],
            WorkingDirectory = "/srv",
            User = "999:999",
            Labels = new Dictionary<string, string> { ["role"] = "web" },
            Mounts = [new MountSpec(MountSpec.Volume, "data", "/data", ReadOnly: true)],
            Start = false,
        });

        var arguments = runner.Invocations[0].Arguments;

        Assert.Contains("--entrypoint", arguments);
        Assert.Contains("/docker-entrypoint.sh", arguments);
        Assert.Contains("--workdir", arguments);
        Assert.Contains("/srv", arguments);
        Assert.Contains("--user", arguments);
        Assert.Contains("999:999", arguments);
        Assert.Contains("role=web", arguments);
        Assert.Contains("data:/data:ro", arguments);

        var image = Array.IndexOf(arguments.ToArray(), "nginx:alpine");
        Assert.Equal(["nginx", "-g", "daemon off;"], arguments.Skip(image + 1));
    }

    /// <summary>
    /// The parts of a multi-part entry point that <c>--entrypoint</c> cannot carry keep their meaning
    /// in front of the command: <c>--entrypoint foo image a b</c> runs <c>foo a b</c>.
    /// </summary>
    [Fact]
    public async Task CreateContainerAsync_folds_a_multi_part_entrypoint_into_the_command()
    {
        var runner = Installed().When(_ => true, output: [DummyId]);

        await Engine(runner).CreateContainerAsync(new CreateContainerRequest
        {
            Image = "alpine:3.20",
            Entrypoint = ["/bin/sh", "-c"],
            Command = ["echo hi"],
            Start = false,
        });

        var arguments = runner.Invocations[0].Arguments;
        var image = Array.IndexOf(arguments.ToArray(), "alpine:3.20");

        Assert.Equal(["--entrypoint", "/bin/sh"], arguments.SkipWhile(a => a != "--entrypoint").Take(2));
        Assert.Equal(["-c", "echo hi"], arguments.Skip(image + 1));
    }

    [Fact]
    public async Task CreateContainerAsync_reads_the_id_from_stdout_and_trims_it()
    {
        var runner = Installed().When(_ => true, output: [$"  {DummyId}  "]);

        var id = await Engine(runner).CreateContainerAsync(
            new CreateContainerRequest { Image = "nginx", Start = false });

        Assert.Equal(DummyId, id);
    }

    // Not observed against real nerdctl 2.3.5 today — the auto-pull progress this method's own doc
    // comment describes goes to stderr, not stdout — but a bare `.Trim()` of the whole blob would fail
    // this the moment it ever did land on stdout, so this pins the defensive "last non-empty line"
    // behaviour rather than trusting it went untested.
    [Fact]
    public async Task CreateContainerAsync_uses_the_last_non_empty_line_if_stdout_has_more_than_one()
    {
        var runner = Installed().When(_ => true, output: ["pulling image...", "", DummyId]);

        var id = await Engine(runner).CreateContainerAsync(
            new CreateContainerRequest { Image = "nginx", Start = false });

        Assert.Equal(DummyId, id);
    }

    [Fact]
    public async Task CreateContainerAsync_throws_rather_than_return_a_null_id_for_empty_stdout()
    {
        var runner = Installed().When(_ => true, output: []);

        var ex = await Assert.ThrowsAsync<EngineException>(
            () => Engine(runner).CreateContainerAsync(
                new CreateContainerRequest { Image = "nginx", Start = false }).AsTask());

        Assert.Contains("printed no id", ex.Message, StringComparison.Ordinal);
    }

    // All four RestartPolicy values, not just Always (which the argument-list test above already
    // exercises) — MapRestart's OnFailure and UnlessStopped branches would go untested otherwise, and
    // swapping those two specific strings would pass every other test in this file.
    [Theory]
    [InlineData(RestartPolicy.No, null)]
    [InlineData(RestartPolicy.Always, "always")]
    [InlineData(RestartPolicy.OnFailure, "on-failure")]
    [InlineData(RestartPolicy.UnlessStopped, "unless-stopped")]
    public async Task CreateContainerAsync_maps_every_RestartPolicy_value(RestartPolicy policy, string? flagValue)
    {
        var runner = Installed().When(_ => true, output: [DummyId]);

        await Engine(runner).CreateContainerAsync(
            new CreateContainerRequest { Image = "nginx", RestartPolicy = policy, Start = false });

        string[] expected = flagValue is null
            ? ["--namespace", "k8s.io", "create", "nginx"]
            : ["--namespace", "k8s.io", "create", "--restart", flagValue, "nginx"];
        Assert.Equal(expected, runner.Invocations[0].Arguments);
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
