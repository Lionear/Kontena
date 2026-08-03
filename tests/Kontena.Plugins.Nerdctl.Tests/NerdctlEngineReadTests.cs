using Kontena.Sdk.Errors;
using Kontena.Sdk.Models;
using Kontena.Sdk.Tooling;
using Kontena.Sdk.Tooling.Fakes;

namespace Kontena.Plugins.Nerdctl.Tests;

/// <summary>
/// <see cref="NerdctlEngine"/>'s read side (KON-141 PR 2 task 6) — containers, images, networks,
/// volumes, inspect and logs, every populated case against the real fixtures captured from nerdctl
/// 2.3.5 (Notes/nerdctl-cli-formats.md), never hand-written JSON, and every edge that file calls out
/// gets its own test rather than being inferred from an "empty" default that would pass either way.
/// </summary>
public sealed class NerdctlEngineReadTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine("Fixtures", name));

    private static FakeToolRunner Installed() => new FakeToolRunner().Install(NerdctlTool.Definition);

    private static NerdctlEngine Engine(IToolRunner runner, string @namespace = "k8s.io") =>
        new(new NerdctlCli(runner, @namespace), $"nerdctl:{@namespace}", $"nerdctl ({@namespace})", @namespace);

    // ── ListContainersAsync ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListContainersAsync_returns_every_row_mapped_from_the_real_capture()
    {
        var runner = Installed().When(_ => true, output: [Fixture("ps.json")]);

        var containers = await Engine(runner).ListContainersAsync(all: true);

        Assert.Equal(4, containers.Count);
        var provisioner = Assert.Single(containers, c => c.Id == "281c109b7ece");
        Assert.Equal("local-path-provisioner", provisioner.Name);
        Assert.Equal(ContainerState.Running, provisioner.State);
        var created = Assert.Single(containers, c => c.Id == "841530983c81");
        Assert.Equal(ContainerState.Created, created.State);
    }

    [Fact]
    public async Task ListContainersAsync_all_true_adds_dash_a()
    {
        var runner = Installed().When(_ => true, output: [Fixture("ps.json")]);

        await Engine(runner).ListContainersAsync(all: true);

        // The argument list is what discriminates this test, not the result — the fake returns the
        // same fixture regardless of what was asked for, so asserting only on the parsed containers
        // would pass even if `-a` were never sent.
        Assert.Equal(
            ["--namespace", "k8s.io", "ps", "-a", "--format", "json"],
            runner.Invocations[^1].Arguments);
    }

    [Fact]
    public async Task ListContainersAsync_all_false_omits_dash_a()
    {
        var runner = Installed().When(_ => true, output: [Fixture("ps.json")]);

        await Engine(runner).ListContainersAsync(all: false);

        Assert.Equal(
            ["--namespace", "k8s.io", "ps", "--format", "json"],
            runner.Invocations[^1].Arguments);
    }

    // ── ListImagesAsync ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListImagesAsync_reads_the_human_size_and_the_real_tag_from_the_capture()
    {
        var runner = Installed().When(_ => true, output: [Fixture("images.json")]);

        var images = await Engine(runner).ListImagesAsync();

        Assert.Equal(4, images.Count);
        var nginx = Assert.Single(images, i => i is { Repository: "nginx", Tag: "1.27-alpine" });
        Assert.Equal(53_980_000L, nginx.SizeBytes); // "53.98MB", not BlobSize's "20.97MB"
        var dangling = Assert.Single(images, i => i is { Repository: "<none>", Tag: "<none>" });
        Assert.Equal("<none>", dangling.Tag);
    }

    // ── ListNetworksAsync ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListNetworksAsync_reads_the_three_networks_from_the_capture()
    {
        var runner = Installed().When(_ => true, output: [Fixture("network-ls.json")]);

        var networks = await Engine(runner).ListNetworksAsync();

        Assert.Equal(["kindnet", "host", "none"], networks.Select(n => n.Name).ToArray());
        Assert.All(networks, n => Assert.Equal(string.Empty, n.Id)); // observed empty for all three
        Assert.True(Assert.Single(networks, n => n.Name == "host").IsBuiltIn);
        Assert.False(Assert.Single(networks, n => n.Name == "kindnet").IsBuiltIn);
    }

    // ── ListVolumesAsync ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListVolumesAsync_on_genuinely_empty_output_returns_an_empty_list_not_an_exception()
    {
        // volume-ls-empty.json is a genuinely empty file (0 bytes) — the ordinary state on a machine
        // with no volumes. Not "[]", not a blank line: nerdctl prints nothing at all.
        var emptyOutput = Fixture("volume-ls-empty.json");
        Assert.Equal(string.Empty, emptyOutput);

        var runner = Installed().When(_ => true, output: [emptyOutput]);

        var volumes = await Engine(runner).ListVolumesAsync();

        Assert.Empty(volumes);
    }

    [Fact]
    public async Task ListVolumesAsync_maps_driver_and_mountpoint_from_the_real_populated_capture()
    {
        var runner = Installed().When(_ => true, output: [Fixture("volume-ls.json")]);

        var volume = Assert.Single(await Engine(runner).ListVolumesAsync());

        Assert.Equal("kontena-probe", volume.Name);
        // VolumeSummary.Driver already defaults to "local", and nerdctl's real capture also happens to
        // say "local" — so Driver alone would pass even if ToVolume never read it. Mountpoint has no
        // such coincidental default, so it is the assertion that actually discriminates a working
        // mapping from a silently unmapped field.
        Assert.Equal("/var/lib/nerdctl/1935db59/volumes/default/kontena-probe/_data", volume.Mountpoint);
        Assert.Equal("local", volume.Driver);
    }

    // ── InspectContainerAsync ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task InspectContainerAsync_maps_the_real_capture()
    {
        var runner = Installed().When(_ => true, output: [Fixture("inspect.json")]);

        var inspect = await Engine(runner).InspectContainerAsync("281c109b7ece");

        Assert.Equal(ContainerState.Running, inspect.State);
        Assert.Equal("local-path-provisioner", inspect.Name); // empty top-level Name falls back to the CRI label
        Assert.Equal(13, inspect.EnvironmentVariables.Count);
        Assert.StartsWith("local-path-provisioner --debug start", inspect.Command, StringComparison.Ordinal);

        Assert.Equal(
            ["--namespace", "k8s.io", "inspect", "281c109b7ece"],
            runner.Invocations[^1].Arguments);
    }

    [Fact]
    public async Task InspectContainerAsync_for_an_unknown_id_translates_the_non_zero_exit_to_ResourceNotFoundException()
    {
        // A container id nerdctl does not know about makes the CLI exit non-zero — NerdctlCli surfaces
        // that as ToolFailedException, a tooling-layer exception naming a raw command line. It must not
        // reach the CEAL boundary unchanged.
        var runner = Installed().When(_ => true, errorOutput: ["no such container: bogus"], exitCode: 1);

        await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => Engine(runner).InspectContainerAsync("bogus").AsTask());
    }

    // ── StreamLogsAsync ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StreamLogsAsync_follow_true_adds_dash_f_and_always_asks_for_timestamps()
    {
        var runner = Installed().When(_ => true, output: []);

        await CollectAsync(Engine(runner).StreamLogsAsync("281c109b7ece", follow: true));

        Assert.Equal(
            ["--namespace", "k8s.io", "logs", "--timestamps", "-f", "281c109b7ece"],
            runner.Invocations[^1].Arguments);
    }

    [Fact]
    public async Task StreamLogsAsync_follow_false_omits_dash_f()
    {
        var runner = Installed().When(_ => true, output: []);

        await CollectAsync(Engine(runner).StreamLogsAsync("281c109b7ece", follow: false));

        Assert.Equal(
            ["--namespace", "k8s.io", "logs", "--timestamps", "281c109b7ece"],
            runner.Invocations[^1].Arguments);
    }

    [Fact]
    public async Task StreamLogsAsync_yields_bare_lines_with_no_wrapper()
    {
        // logs-timestamped.txt is a real `nerdctl logs --timestamps` capture — pins the line shape
        // (RFC3339 with nanoseconds, one space, then the message) against observed output rather than
        // a hand-typed string built to match the assumption.
        var rawLines = Fixture("logs-timestamped.txt")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .ToArray();
        var runner = Installed().When(_ => true, output: rawLines);

        var entries = await CollectAsync(Engine(runner).StreamLogsAsync("281c109b7ece", follow: false));

        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal(LogSource.Stdout, e.Source));

        var first = entries[0];
        Assert.Equal(
            "I0802 08:42:00.944686       1 controller.go:838] \"Starting provisioner controller\"",
            first.Message);
        Assert.Equal(
            new DateTimeOffset(2026, 8, 2, 8, 42, 0, TimeSpan.Zero), first.Timestamp, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task StreamLogsAsync_for_an_unknown_id_translates_the_non_zero_exit_to_ResourceNotFoundException()
    {
        var runner = Installed().When(_ => true, errorOutput: ["no such container: bogus"], exitCode: 1);

        await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => CollectAsync(Engine(runner).StreamLogsAsync("bogus")));
    }

    private static async Task<List<LogEntry>> CollectAsync(IAsyncEnumerable<LogEntry> entries)
    {
        var result = new List<LogEntry>();
        await foreach (var entry in entries)
            result.Add(entry);
        return result;
    }
}
