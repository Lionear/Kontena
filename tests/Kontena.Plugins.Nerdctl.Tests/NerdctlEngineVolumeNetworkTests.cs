using Kontena.Sdk.Errors;
using Kontena.Sdk.Models;
using Kontena.Sdk.Tooling;
using Kontena.Sdk.Tooling.Fakes;

namespace Kontena.Plugins.Nerdctl.Tests;

/// <summary>
/// <see cref="NerdctlEngine"/>'s volume and network write side (KON-141 PR 3 task 3). The asymmetry
/// Notes/nerdctl-write-formats.md records drives most of this: <c>volume create</c> only echoes the
/// name, <c>network create</c> echoes a full 64-character id, and neither carries the fields
/// <see cref="VolumeSummary"/>/<see cref="NetworkSummary"/> need — so both create paths are only proven
/// correct by asserting a field the create command could not possibly have supplied
/// (<see cref="VolumeSummary.Mountpoint"/>, <see cref="NetworkSummary.Id"/> from the `ls` row, not the
/// id `create` printed), never <see cref="VolumeSummary.Driver"/> alone, which already matches the SDK's
/// own default and would pass whether or not the read-back ever ran.
/// </summary>
public sealed class NerdctlEngineVolumeNetworkTests
{
    // Dummy identifiers distinct from any input used below, so a hard-coded value could not pass by
    // coincidence.
    private const string DummyNetworkCreateId =
        "f00dface00112233445566778899aabbccddeeff00112233445566778899aa";
    private const string DummyNetworkLsId = "9f8e7d6c5b4a";
    private const string DummyMountpoint = "/var/lib/nerdctl/deadbeef/volumes/default/probe-vol/_data";

    private static FakeToolRunner Installed() => new FakeToolRunner().Install(NerdctlTool.Definition);

    private static NerdctlEngine Engine(IToolRunner runner, string @namespace = "k8s.io") =>
        new(new NerdctlCli(runner, @namespace), $"nerdctl:{@namespace}", $"nerdctl ({@namespace})", @namespace);

    // ── CreateVolumeAsync ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVolumeAsync_builds_the_argument_list_from_the_request()
    {
        var runner = Installed()
            .When(inv => inv.Arguments.Contains("ls"), output: [
                $$"""{"Driver":"local","Labels":"","Mountpoint":"{{DummyMountpoint}}","Name":"probe-vol","Scope":"local","Size":""}""",
            ])
            .When(_ => true, output: ["probe-vol"]);

        await Engine(runner).CreateVolumeAsync(new CreateVolumeRequest
        {
            Name = "probe-vol",
            Labels = new Dictionary<string, string> { ["team"] = "kontena" },
        });

        Assert.Equal(
            ["--namespace", "k8s.io", "volume", "create", "--label", "team=kontena", "probe-vol"],
            runner.Invocations[0].Arguments);
    }

    [Fact]
    public async Task CreateVolumeAsync_never_sends_a_dash_dash_driver_flag_even_when_the_request_names_one()
    {
        // A real capture against nerdctl 2.3.5 shows `volume create --help` lists only `--label` — no
        // `--driver` at all — and passing `--driver` anyway is fatal ("unknown flag: --driver"), not
        // silently ignored by nerdctl. A request naming a driver other than the SDK default must
        // therefore never reach the command line; nerdctl only ever creates the one driver it has.
        var runner = Installed()
            .When(inv => inv.Arguments.Contains("ls"), output: [
                $$"""{"Driver":"local","Labels":"","Mountpoint":"{{DummyMountpoint}}","Name":"probe-vol","Scope":"local","Size":""}""",
            ])
            .When(_ => true, output: ["probe-vol"]);

        await Engine(runner).CreateVolumeAsync(new CreateVolumeRequest { Name = "probe-vol", Driver = "overlayfs" });

        Assert.Equal(
            ["--namespace", "k8s.io", "volume", "create", "probe-vol"],
            runner.Invocations[0].Arguments);
        Assert.DoesNotContain("--driver", runner.Invocations[0].Arguments);
        Assert.DoesNotContain("overlayfs", runner.Invocations[0].Arguments);
    }

    [Fact]
    public async Task CreateVolumeAsync_reads_the_summary_back_from_volume_ls_rather_than_the_request()
    {
        // `volume create` only ever echoes the name (Notes/nerdctl-write-formats.md) — no driver, no
        // mountpoint — so Mountpoint here can only have come from the `volume ls` read-back, never from
        // constructing a VolumeSummary out of the request, which supplied no mountpoint at all.
        var runner = Installed()
            .When(inv => inv.Arguments.Contains("ls"), output: [
                $$"""{"Driver":"local","Labels":"","Mountpoint":"{{DummyMountpoint}}","Name":"probe-vol","Scope":"local","Size":""}""",
            ])
            .When(_ => true, output: ["probe-vol"]);

        var summary = await Engine(runner).CreateVolumeAsync(new CreateVolumeRequest { Name = "probe-vol" });

        Assert.Equal("probe-vol", summary.Name);
        Assert.Equal(DummyMountpoint, summary.Mountpoint);
    }

    [Fact]
    public async Task CreateVolumeAsync_translates_a_missing_binary_into_the_shared_engine_exception()
    {
        await Assert.ThrowsAsync<EngineUnreachableException>(
            () => Engine(new FakeToolRunner())
                .CreateVolumeAsync(new CreateVolumeRequest { Name = "probe-vol" })
                .AsTask());
    }

    [Fact]
    public async Task CreateVolumeAsync_for_a_generic_failure_throws_EngineException_with_nerdctls_message()
    {
        var runner = Installed().When(_ => true, errorOutput: ["a volume named \"probe-vol\" already exists"], exitCode: 1);

        var ex = await Assert.ThrowsAsync<EngineException>(
            () => Engine(runner).CreateVolumeAsync(new CreateVolumeRequest { Name = "probe-vol" }).AsTask());

        Assert.IsNotType<ResourceNotFoundException>(ex);
        Assert.Contains("already exists", ex.Message, StringComparison.Ordinal);
    }

    // ── RemoveVolumeAsync ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RemoveVolumeAsync_force_false_runs_volume_rm_without_dash_f()
    {
        var runner = Installed().When(_ => true, output: ["probe-vol"]);

        await Engine(runner).RemoveVolumeAsync("probe-vol");

        Assert.Equal(
            ["--namespace", "k8s.io", "volume", "rm", "probe-vol"],
            runner.Invocations[^1].Arguments);
    }

    [Fact]
    public async Task RemoveVolumeAsync_force_true_adds_dash_f()
    {
        var runner = Installed().When(_ => true, output: ["probe-vol"]);

        await Engine(runner).RemoveVolumeAsync("probe-vol", force: true);

        Assert.Equal(
            ["--namespace", "k8s.io", "volume", "rm", "-f", "probe-vol"],
            runner.Invocations[^1].Arguments);
    }

    [Fact]
    public async Task RemoveVolumeAsync_tells_a_missing_volume_apart_from_a_busy_one_despite_the_identical_fatal_line()
    {
        // A real capture against nerdctl 2.3.5 shows both cases end in the exact same fatal line —
        // "some volumes could not be removed" — whether the volume never existed or is still mounted.
        // Only the warning line above it differs ("...": not found" versus "is in use (failed
        // precondition)"). This test pins that RemoveVolumeAsync still tells the two apart correctly by
        // matching on that warning, not the shared fatal line a naive implementation might key on.
        const string fatal = "level=fatal msg=\"some volumes could not be removed\"";

        var missing = Installed().When(_ => true,
            errorOutput: ["level=warning msg=\"volume \\\"bogus\\\": not found\"", fatal],
            exitCode: 1);
        var busy = Installed().When(_ => true,
            errorOutput: ["level=warning msg=\"volume \\\"probe-vol\\\" is in use (failed precondition)\"", fatal],
            exitCode: 1);

        await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => Engine(missing).RemoveVolumeAsync("bogus").AsTask());

        var busyEx = await Assert.ThrowsAsync<EngineException>(
            () => Engine(busy).RemoveVolumeAsync("probe-vol").AsTask());
        Assert.IsNotType<ResourceNotFoundException>(busyEx);
        Assert.Contains("could not be removed", busyEx.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoveVolumeAsync_translates_a_missing_binary_into_the_shared_engine_exception()
    {
        await Assert.ThrowsAsync<EngineUnreachableException>(
            () => Engine(new FakeToolRunner()).RemoveVolumeAsync("probe-vol").AsTask());
    }

    // ── CreateNetworkAsync ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateNetworkAsync_builds_the_argument_list_from_the_request()
    {
        var runner = Installed()
            .When(inv => inv.Arguments.Contains("ls"), output: [
                $$"""{"ID":"{{DummyNetworkLsId}}","Name":"probe-net","Labels":""}""",
            ])
            .When(_ => true, output: [DummyNetworkCreateId]);

        await Engine(runner).CreateNetworkAsync(new CreateNetworkRequest
        {
            Name = "probe-net",
            Driver = "macvlan",
            Subnet = "10.10.0.0/24",
        });

        Assert.Equal(
            ["--namespace", "k8s.io", "network", "create", "--driver", "macvlan", "--subnet", "10.10.0.0/24", "probe-net"],
            runner.Invocations[0].Arguments);
    }

    [Fact]
    public async Task CreateNetworkAsync_with_the_default_driver_and_no_subnet_omits_both_flags()
    {
        var runner = Installed()
            .When(inv => inv.Arguments.Contains("ls"), output: [
                $$"""{"ID":"{{DummyNetworkLsId}}","Name":"probe-net","Labels":""}""",
            ])
            .When(_ => true, output: [DummyNetworkCreateId]);

        await Engine(runner).CreateNetworkAsync(new CreateNetworkRequest { Name = "probe-net" });

        Assert.Equal(
            ["--namespace", "k8s.io", "network", "create", "probe-net"],
            runner.Invocations[0].Arguments);
    }

    [Fact]
    public async Task CreateNetworkAsync_reads_the_summary_back_from_network_ls_rather_than_creates_stdout()
    {
        // `network create` prints the full 64-character id (DummyNetworkCreateId here); `network ls`
        // reports a different, short id for the same network (Notes/nerdctl-write-formats.md). The
        // returned summary must carry the `ls` id, not the one `create` printed — proof the read-back
        // ran rather than the id being lifted straight off stdout the way CreateContainerAsync does.
        var runner = Installed()
            .When(inv => inv.Arguments.Contains("ls"), output: [
                $$"""{"ID":"{{DummyNetworkLsId}}","Name":"probe-net","Labels":""}""",
            ])
            .When(_ => true, output: [DummyNetworkCreateId]);

        var summary = await Engine(runner).CreateNetworkAsync(new CreateNetworkRequest { Name = "probe-net" });

        Assert.Equal("probe-net", summary.Name);
        Assert.Equal(DummyNetworkLsId, summary.Id);
        Assert.NotEqual(DummyNetworkCreateId, summary.Id);
    }

    [Fact]
    public async Task CreateNetworkAsync_translates_a_missing_binary_into_the_shared_engine_exception()
    {
        await Assert.ThrowsAsync<EngineUnreachableException>(
            () => Engine(new FakeToolRunner())
                .CreateNetworkAsync(new CreateNetworkRequest { Name = "probe-net" })
                .AsTask());
    }

    [Fact]
    public async Task CreateNetworkAsync_for_a_generic_failure_throws_EngineException_with_nerdctls_message()
    {
        var runner = Installed().When(_ => true, errorOutput: ["a network named \"probe-net\" already exists"], exitCode: 1);

        var ex = await Assert.ThrowsAsync<EngineException>(
            () => Engine(runner).CreateNetworkAsync(new CreateNetworkRequest { Name = "probe-net" }).AsTask());

        Assert.IsNotType<ResourceNotFoundException>(ex);
        Assert.Contains("already exists", ex.Message, StringComparison.Ordinal);
    }

    // ── RemoveNetworkAsync ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RemoveNetworkAsync_runs_network_rm_with_the_given_id()
    {
        var runner = Installed().When(_ => true, output: ["probe-net"]);

        await Engine(runner).RemoveNetworkAsync("probe-net");

        Assert.Equal(
            ["--namespace", "k8s.io", "network", "rm", "probe-net"],
            runner.Invocations[^1].Arguments);
    }

    [Fact]
    public async Task RemoveNetworkAsync_for_an_unknown_id_throws_ResourceNotFoundException()
    {
        // Real capture against nerdctl 2.3.5: unlike volume rm, the not-found case here is unambiguous
        // on its own — "no network found matching:" appears in no other observed failure.
        var runner = Installed().When(_ => true,
            errorOutput: [
                "level=error msg=\"no network found matching: bogus\"",
                "level=fatal msg=\"no network could be removed\"",
            ],
            exitCode: 1);

        await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => Engine(runner).RemoveNetworkAsync("bogus").AsTask());
    }

    [Fact]
    public async Task RemoveNetworkAsync_translates_a_missing_binary_into_the_shared_engine_exception()
    {
        await Assert.ThrowsAsync<EngineUnreachableException>(
            () => Engine(new FakeToolRunner()).RemoveNetworkAsync("probe-net").AsTask());
    }
}
