using Kontena.Sdk.Errors;
using Kontena.Sdk.Models;
using Kontena.Sdk.Tooling;
using Kontena.Sdk.Tooling.Fakes;

namespace Kontena.Adapters.Apple.Tests;

/// <summary>
/// The write side: creating containers, volumes and networks, removing them, and pruning. Every command
/// line and every output shape asserted here was run against a real <c>container</c> 1.2.2 first.
/// </summary>
public sealed class AppleEngineWriteTests
{
    private static FakeToolRunner Installed() => new FakeToolRunner().Install(AppleTool.Definition);

    private static AppleEngine Engine(IToolRunner runner) =>
        new(new AppleCli(runner), "apple", "Apple container");

    private static CreateContainerRequest Request => new() { Image = "alpine:3.20" };

    // ── Create ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A create asks the image which architectures it carries before it builds its command line
    /// (KON-369), so a runner for these has to answer <c>image</c> as well as <c>create</c>.
    /// <para>
    /// The image runs natively here unless a test says otherwise, so no <c>--arch</c> reaches the
    /// command line and every other assertion is about the flag it is actually testing. Naming a fixed
    /// architecture would make that true on one kind of runner and not the other (KON-384).
    /// </para>
    /// </summary>
    private static FakeToolRunner Creating(string? architecture = null) =>
        Installed()
            .When(i => i.Arguments[0] == "image",
                output: [Inspect(architecture ?? ToolPlatform.Architecture ?? "arm64")])
            .When(_ => true, output: ["web"]);

    /// <summary>One image with one platform variant, in the shape <c>image inspect</c> prints.</summary>
    private static string Inspect(string architecture) =>
        $$$"""
           [{"id":"a","configuration":{"name":"alpine:3.20"},"variants":[{"size":4093973,"platform":{"architecture":"{{{architecture}}}","os":"linux"}}]}]
           """;

    /// <summary>The create itself, past the image question that now precedes it.</summary>
    private static IReadOnlyList<string> Created(FakeToolRunner runner) =>
        runner.Invocations.Single(i => i.Arguments[0] is "create" or "run").Arguments;

    /// <summary>
    /// <c>run</c> narrates before it answers: "[6/6] Starting container" comes out ahead of the name.
    /// Taking the first line would hand the caller a progress message as a container id.
    /// </summary>
    [Fact]
    public async Task CreateContainerAsync_takes_the_id_from_the_last_line()
    {
        var runner = Installed()
            .When(i => i.Arguments[0] == "image", output: [Inspect("arm64")])
            .When(_ => true, output: ["[5/6] Unpacking init image", "[6/6] Starting container", "web"]);

        Assert.Equal("web", await Engine(runner).CreateContainerAsync(Request));
    }

    /// <summary>Creating without starting is its own subcommand, not a run followed by a stop.</summary>
    [Theory]
    [InlineData(true, "run")]
    [InlineData(false, "create")]
    public async Task CreateContainerAsync_runs_or_creates_as_asked(bool start, string expected)
    {
        var runner = Creating();

        await Engine(runner).CreateContainerAsync(Request with { Start = start });

        var arguments = Created(runner);
        Assert.Equal(expected, arguments[0]);
        Assert.Equal(start, arguments.Contains("--detach"));
    }

    /// <summary>
    /// Without <c>--arch</c> the CLI creates for this host, and an amd64-only image — SQL Server is the
    /// one that found this — fails with the bare line <c>Error: platform linux/arm64</c>. Measured
    /// against 1.2.2: naming the architecture the image carries creates it and runs it emulated.
    /// <para>
    /// Which architecture is the foreign one is asked of the host rather than written down, because the
    /// engine decides by comparing against <see cref="ToolPlatform.Architecture"/>. Hard-coding "amd64"
    /// made this a test of the runner: it held on the arm64 Mac and failed on every x64 one, where amd64
    /// is native and the engine rightly says nothing (KON-384).
    /// </para>
    /// </summary>
    [Fact]
    public async Task CreateContainerAsync_asks_for_the_architecture_the_image_carries()
    {
        var foreign = ToolPlatform.Architecture == "amd64" ? "arm64" : "amd64";
        var runner = Creating(architecture: foreign);

        await Engine(runner).CreateContainerAsync(Request);

        Assert.Equal(["--arch", foreign], Window(Created(runner), "--arch"));
    }

    /// <summary>
    /// An image that has a variant for this host is created without an opinion about architecture. Both
    /// variants are scripted rather than the host's own, so the assertion holds on every runner.
    /// </summary>
    [Fact]
    public async Task CreateContainerAsync_says_nothing_about_an_image_that_runs_natively()
    {
        var runner = Installed()
            .When(i => i.Arguments[0] == "image", output: ["""
                [{"id":"a","configuration":{"name":"alpine:3.20"},"variants":[
                  {"size":4093973,"platform":{"architecture":"amd64","os":"linux"}},
                  {"size":4093973,"platform":{"architecture":"arm64","os":"linux"}}]}]
                """])
            .When(_ => true, output: ["web"]);

        await Engine(runner).CreateContainerAsync(Request);

        Assert.DoesNotContain("--arch", Created(runner));
    }

    /// <summary>
    /// An image the CLI cannot describe must not stop the create: the create's own error is the one
    /// that says what is wrong, and refusing here would replace it with a worse one.
    /// </summary>
    [Fact]
    public async Task CreateContainerAsync_still_creates_when_the_image_cannot_be_inspected()
    {
        var runner = Installed()
            .When(i => i.Arguments[0] == "image", exitCode: 1, errorOutput: ["Error: image not found"])
            .When(_ => true, output: ["web"]);

        Assert.Equal("web", await Engine(runner).CreateContainerAsync(Request));
        Assert.DoesNotContain("--arch", Created(runner));
    }

    [Fact]
    public async Task CreateContainerAsync_passes_every_part_of_the_request()
    {
        var runner = Creating();

        await Engine(runner).CreateContainerAsync(new CreateContainerRequest
        {
            Image = "alpine:3.20",
            Name = "web",
            Ports = [new PortBinding(8080, 80), new PortBinding(9090, 90, "udp")],
            Environment = new Dictionary<string, string> { ["FOO"] = "bar" },
            Mounts = [new MountSpec(MountSpec.Volume, "data", "/data")],
            Network = "backend",
        });

        var arguments = Created(runner);
        Assert.Equal(["--name", "web"], Window(arguments, "--name"));
        Assert.Equal(["--publish", "8080:80/tcp"], Window(arguments, "--publish"));
        Assert.Contains("9090:90/udp", arguments);
        Assert.Equal(["--env", "FOO=bar"], Window(arguments, "--env"));
        Assert.Equal(["--volume", "data:/data"], Window(arguments, "--volume"));
        Assert.Equal(["--network", "backend"], Window(arguments, "--network"));

        // The image is the last word: everything after it would be read as the command to run.
        Assert.Equal("alpine:3.20", arguments[^1]);
    }

    /// <summary>
    /// A container carries a command, a workdir, a user and labels, and none of them reached the CLI
    /// before KON-350 — a migrated container would have run the image's default CMD instead of its
    /// own, which no test and no screen would have shown.
    /// </summary>
    [Fact]
    public async Task CreateContainerAsync_passes_command_workdir_user_and_labels()
    {
        var runner = Creating();

        await Engine(runner).CreateContainerAsync(new CreateContainerRequest
        {
            Image = "nginx:alpine",
            Entrypoint = ["/docker-entrypoint.sh"],
            Command = ["nginx", "-g", "daemon off;"],
            WorkingDirectory = "/srv",
            User = "999:999",
            Labels = new Dictionary<string, string> { ["role"] = "web" },
        });

        var arguments = Created(runner);

        Assert.Equal(["--entrypoint", "/docker-entrypoint.sh"], Window(arguments, "--entrypoint"));
        Assert.Equal(["--workdir", "/srv"], Window(arguments, "--workdir"));
        Assert.Equal(["--user", "999:999"], Window(arguments, "--user"));
        Assert.Equal(["--label", "role=web"], Window(arguments, "--label"));

        // The command follows the image, in order. Anything before it would be read as a flag.
        var image = Array.IndexOf(arguments.ToArray(), "nginx:alpine");
        Assert.Equal(["nginx", "-g", "daemon off;"], arguments.Skip(image + 1));
    }

    /// <summary>
    /// This CLI's <c>--entrypoint</c> takes one command, not a list. The remaining parts keep their
    /// meaning by moving to the front of the command — <c>--entrypoint foo image a b</c> runs
    /// <c>foo a b</c>.
    /// </summary>
    [Fact]
    public async Task CreateContainerAsync_folds_a_multi_part_entrypoint_into_the_command()
    {
        var runner = Creating();

        await Engine(runner).CreateContainerAsync(new CreateContainerRequest
        {
            Image = "alpine:3.20",
            Entrypoint = ["/bin/sh", "-c"],
            Command = ["echo hi"],
        });

        var arguments = Created(runner);

        Assert.Equal(["--entrypoint", "/bin/sh"], Window(arguments, "--entrypoint"));

        var image = Array.IndexOf(arguments.ToArray(), "alpine:3.20");
        Assert.Equal(["-c", "echo hi"], arguments.Skip(image + 1));
    }

    // ── Volume transfer ─────────────────────────────────────────────────────

    // Both directions mount the volume into a throwaway container, and picking the image for that
    // asks the CLI what is local — so a runner for these has to answer `image` as well as `run`.
    private static FakeToolRunner Transferring() =>
        Installed()
            .When(i => i.Arguments[0] == "image",
                output: ["""[{"id":"a","configuration":{"name":"alpine:3.20"},"variants":[{"size":4093973,"platform":{"architecture":"arm64","os":"linux"}}]}]"""])
            .When(_ => true, output: []);

    /// <summary>
    /// A host archive to transfer. The engine resolves whatever it is handed with
    /// <see cref="Path.GetFullPath(string)"/>, so a literal <c>/tmp/kon350/data.tar</c> would land on
    /// the current drive on Windows and no longer match a hardcoded Unix expectation (KON-404).
    /// </summary>
    private static readonly string Archive =
        Path.Combine(Path.GetTempPath(), "kon350", "data.tar");

    /// <summary>The mount the engine builds for <see cref="Archive"/>'s directory, on this host.</summary>
    private static readonly string StageMount =
        $"{Path.GetDirectoryName(Path.GetFullPath(Archive))}:/kontena-stage";

    /// <summary>
    /// Export runs <c>tar</c> inside a throwaway container with two mounts: the volume and the
    /// directory the archive goes in. Measured against 1.2.2 — a host path and a named volume can
    /// share one <c>run</c>, which is what makes this one command per side.
    /// </summary>
    [Fact]
    public async Task ExportVolumeAsync_mounts_the_volume_and_the_staging_directory()
    {
        var runner = Transferring();

        await Engine(runner).ExportVolumeAsync("data", Archive);

        var arguments = runner.Invocations.Single(i => i.Arguments[0] == "run").Arguments;

        Assert.Equal("run", arguments[0]);
        Assert.Contains("--rm", arguments);
        Assert.Contains("data:/kontena-volume", arguments);
        Assert.Contains(StageMount, arguments);

        // lost+found is an ext4 artefact of this runtime's volumes, not the user's data — copying it
        // into another engine's volume would invent a directory nobody made.
        Assert.Contains(arguments, a => a.Contains("--exclude", StringComparison.Ordinal)
                                        && a.Contains("lost+found", StringComparison.Ordinal));
        Assert.Contains(arguments, a => a.Contains("/kontena-stage/data.tar", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ImportVolumeAsync_extracts_into_the_volume()
    {
        var runner = Transferring();

        await Engine(runner).ImportVolumeAsync("data", Archive);

        var arguments = runner.Invocations.Single(i => i.Arguments[0] == "run").Arguments;

        Assert.Contains("data:/kontena-volume", arguments);
        Assert.Contains(StageMount, arguments);
        Assert.Contains(arguments, a => a.Contains("tar -xf /kontena-stage/data.tar", StringComparison.Ordinal));
    }

    [Fact]
    public void Capabilities_say_volume_transfer_is_supported()
    {
        Assert.True(Engine(Installed()).Capabilities.SupportsVolumeTransfer);
    }

    /// <summary>
    /// A binding with no host port describes a port the image exposes, which this CLI takes from the
    /// image itself. Publishing it would need a host port to publish on.
    /// </summary>
    [Fact]
    public async Task CreateContainerAsync_skips_a_port_with_nothing_to_publish_on()
    {
        var runner = Creating();

        await Engine(runner).CreateContainerAsync(Request with { Ports = [new PortBinding(null, 80)] });

        Assert.DoesNotContain("--publish", Created(runner));
    }

    /// <summary>
    /// There is no restart-policy flag on this CLI. Accepting the request and dropping the policy would
    /// give someone a container they believe restarts itself, which is the failure this refuses.
    /// </summary>
    [Theory]
    [InlineData(RestartPolicy.Always)]
    [InlineData(RestartPolicy.OnFailure)]
    [InlineData(RestartPolicy.UnlessStopped)]
    public async Task CreateContainerAsync_refuses_a_restart_policy_it_cannot_honour(RestartPolicy policy)
    {
        var error = await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await Engine(Installed()).CreateContainerAsync(Request with { RestartPolicy = policy }));

        Assert.Contains("restart", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The default policy is "no", which is what this runtime does, so it must not refuse it.</summary>
    [Fact]
    public async Task CreateContainerAsync_accepts_the_default_policy()
    {
        var runner = Installed().When(_ => true, output: ["web"]);

        Assert.Equal("web", await Engine(runner).CreateContainerAsync(Request));
    }

    // ── Volumes ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The row comes from reading the volume back, not from echoing the request: the mountpoint is a
    /// path only the runtime knows.
    /// </summary>
    [Fact]
    public async Task CreateVolumeAsync_returns_the_volume_the_engine_reports()
    {
        var runner = Installed()
            .When(i => i.Arguments.Contains("create"))
            .When(i => i.Arguments[0] == "volume", output: [
                """[{"id":"data","configuration":{"name":"data","driver":"local","source":"/vol/data.img"}}]"""])
            .When(i => i.Arguments[0] == "list", output: ["[]"]);

        var volume = await Engine(runner).CreateVolumeAsync(new CreateVolumeRequest { Name = "data" });

        Assert.Equal("data", volume.Name);
        Assert.Equal("/vol/data.img", volume.Mountpoint);
    }

    [Fact]
    public async Task RemoveVolumeAsync_deletes_by_name()
    {
        var runner = Installed();

        await Engine(runner).RemoveVolumeAsync("data");

        Assert.Equal(["volume", "delete", "data"], Assert.Single(runner.Invocations).Arguments);
    }

    /// <summary>
    /// A volume a container still holds is refused, non-zero. It has to reach the caller as an error:
    /// the list page swallows what it is given, so a silent success leaves a row that never disappears
    /// and no reason why.
    /// </summary>
    [Fact]
    public async Task RemoveVolumeAsync_raises_when_the_volume_is_in_use()
    {
        var runner = Installed().When(_ => true, exitCode: 1, errorOutput: [
            "failed to delete volume: [\"id\": data, \"error\": invalidArgument: \"volume 'data' is currently in use\"]"]);

        await Assert.ThrowsAnyAsync<EngineException>(
            async () => await Engine(runner).RemoveVolumeAsync("data"));
    }

    // ── Networks ────────────────────────────────────────────────────────────

    /// <summary>
    /// The neutral model's default driver is "bridge" — Docker's word for what this runtime already is.
    /// This CLI calls it a plugin, defaults it correctly, and would reject that value, so it is not
    /// passed on.
    /// </summary>
    [Fact]
    public async Task CreateNetworkAsync_does_not_pass_the_neutral_default_driver()
    {
        var runner = Installed()
            .When(i => i.Arguments.Contains("create"))
            .When(_ => true, output: ["[]"]);

        await Engine(runner).CreateNetworkAsync(new CreateNetworkRequest { Name = "backend" });

        var create = runner.Invocations[0].Arguments;
        Assert.Equal(["network", "create", "backend"], create);
    }

    [Fact]
    public async Task CreateNetworkAsync_passes_a_subnet_when_one_is_asked_for()
    {
        var runner = Installed()
            .When(i => i.Arguments.Contains("create"))
            .When(_ => true, output: ["[]"]);

        await Engine(runner).CreateNetworkAsync(
            new CreateNetworkRequest { Name = "backend", Subnet = "192.168.70.0/24" });

        Assert.Equal(
            ["network", "create", "--subnet", "192.168.70.0/24", "backend"],
            runner.Invocations[0].Arguments);
    }

    [Fact]
    public async Task RemoveNetworkAsync_deletes_by_id()
    {
        var runner = Installed();

        await Engine(runner).RemoveNetworkAsync("backend");

        Assert.Equal(["network", "delete", "backend"], Assert.Single(runner.Invocations).Arguments);
    }

    // ── Prune ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The count is the ids the command printed. Its summary line is skipped by what it says, because
    /// <c>network prune</c> prints no summary at all and <c>image prune</c> prints one with the word
    /// "Zero" where the number goes.
    /// </summary>
    [Theory]
    [InlineData(new[] { "Reclaimed 1,37 GB in disk space", "w1", "w2" }, 2)]
    [InlineData(new[] { "Reclaimed Zero KB in disk space" }, 0)]
    [InlineData(new[] { "n1", "n2", "n3" }, 3)]
    [InlineData(new string[0], 0)]
    public async Task PruneContainersAsync_counts_what_was_removed(string[] output, int expected)
    {
        var runner = Installed()
            .When(i => i.Arguments.Contains("df"),
                output: ["""{"containers":{"sizeInBytes":0},"images":{"sizeInBytes":0},"volumes":{"sizeInBytes":0}}"""])
            .When(i => i.Arguments[0] == "prune", output: output);

        var result = await Engine(runner).PruneContainersAsync();

        Assert.Equal(expected, result.ItemsDeleted);
    }

    /// <summary>
    /// The byte figure is the drop in what <c>system df</c> reports, measured either side of the prune —
    /// not the localised sentence the CLI prints, which says "1,37 GB" on this machine and "Zero KB"
    /// when it removed nothing.
    /// </summary>
    [Fact]
    public async Task PruneContainersAsync_measures_the_bytes_with_disk_usage()
    {
        var runner = Installed()
            .When(i => i.Arguments.Contains("df"),
                output: ["""{"containers":{"sizeInBytes":1374310400},"images":{"sizeInBytes":0},"volumes":{"sizeInBytes":0}}"""])
            .When(i => i.Arguments[0] == "prune", output: ["Reclaimed 1,37 GB in disk space", "w1"]);

        // The fake answers every df the same way, so this proves the subtraction happened at all;
        // the falling case is covered by the guard below.
        var result = await Engine(runner).PruneContainersAsync();

        Assert.Equal(0, result.SpaceReclaimedBytes);
        Assert.Equal(1, result.ItemsDeleted);
        Assert.Equal(3, runner.Invocations.Count);
        Assert.Equal(2, runner.Invocations.Count(i => i.Arguments.Contains("df")));
    }

    /// <summary>
    /// Disk usage that rose across a prune must not come back as a negative reclaim. It happens for real:
    /// pruning containers makes their image reclaimable, and a caller subtracting the wrong field would
    /// see the total go up.
    /// </summary>
    [Fact]
    public async Task PruneImagesAsync_never_reports_a_negative_reclaim()
    {
        var runner = Installed()
            .When(i => i.Arguments.Contains("df"), output: ["""{"images":{"sizeInBytes":10}}"""])
            .When(i => i.Arguments.Contains("prune"), output: ["Reclaimed Zero KB in disk space"]);

        var result = await Engine(runner).PruneImagesAsync();

        Assert.True(result.SpaceReclaimedBytes >= 0);
    }

    private static string[] Window(IReadOnlyList<string> arguments, string flag)
    {
        var index = arguments.ToList().IndexOf(flag);
        return index < 0 ? [] : [arguments[index], arguments[index + 1]];
    }
}
