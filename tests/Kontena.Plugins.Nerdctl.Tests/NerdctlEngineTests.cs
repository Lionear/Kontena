using Kontena.Sdk;
using Kontena.Sdk.Errors;
using Kontena.Sdk.Models;
using Kontena.Sdk.Tooling;
using Kontena.Sdk.Tooling.Fakes;

namespace Kontena.Plugins.Nerdctl.Tests;

/// <summary>
/// <see cref="NerdctlEngine"/>'s identity, reachability and honest capabilities (KON-141 PR 2 task 5),
/// plus the shrinking list of members this backend refuses outright. The fixture-backed
/// tests here pin the two things a test asserting a bare default would not actually prove: that
/// <see cref="EngineCapabilities.Rootless"/> flips on <c>name=rootless</c> appearing in
/// <c>SecurityOptions</c> — the field <c>info</c> does not have — and that <see cref="BackendInfo.Version"/>
/// / <see cref="BackendInfo.Endpoint"/> carry real values read off the captured fixture, not whatever
/// the record's own defaults happen to be.
/// </summary>
public sealed class NerdctlEngineTests
{
    private static readonly string InfoFixture = File.ReadAllText(Path.Combine("Fixtures", "info.json"));

    // Derived from the real capture rather than hand-written: the one entry the brief calls out as the
    // trap ("name=rootless" is not a field of `info` at all) added to the array actually observed.
    private static readonly string RootlessInfoFixture = InfoFixture.Replace(
        """["name=seccomp,profile=builtin","name=cgroupns"]""",
        """["name=seccomp,profile=builtin","name=cgroupns","name=rootless"]""",
        StringComparison.Ordinal);

    private static FakeToolRunner Installed() => new FakeToolRunner().Install(NerdctlTool.Definition);

    private static NerdctlEngine Engine(IToolRunner runner, string @namespace = "k8s.io") =>
        new(new NerdctlCli(runner, @namespace), $"nerdctl:{@namespace}", $"nerdctl ({@namespace})", @namespace);

    [Fact]
    public void Backend_is_the_id_it_was_constructed_with()
    {
        Assert.Equal("nerdctl:k8s.io", Engine(new FakeToolRunner()).Backend);
    }

    [Fact]
    public async Task PingAsync_succeeds_when_nerdctl_answers()
    {
        var runner = Installed().When(_ => true, output: [InfoFixture]);

        await Engine(runner).PingAsync();

        // No exception is the assertion; also pin the exact command this relies on.
        Assert.Equal(
            ["--namespace", "k8s.io", "info", "--format", "json"],
            runner.Invocations[^1].Arguments);
    }

    [Fact]
    public async Task PingAsync_translates_a_missing_binary_into_the_shared_engine_exception()
    {
        // Nothing installed at all -> NerdctlCli surfaces ToolNotFoundException; PingAsync must not let
        // that leak past the CEAL boundary, since BackendRegistry.ProbeAsync counts "not connected" by
        // catching EngineException-shaped failures the same way for every adapter.
        await Assert.ThrowsAsync<EngineUnreachableException>(
            () => Engine(new FakeToolRunner()).PingAsync().AsTask());
    }

    [Fact]
    public async Task PingAsync_translates_a_non_zero_exit_into_the_shared_engine_exception()
    {
        var runner = Installed().When(_ => true, errorOutput: ["containerd: connection refused"], exitCode: 1);

        await Assert.ThrowsAsync<EngineUnreachableException>(() => Engine(runner).PingAsync().AsTask());
    }

    [Fact]
    public async Task PingAsync_on_malformed_json_throws_EngineUnreachableException_not_a_raw_JsonException()
    {
        // A JSON syntax error escaping NerdctlJson.Parse would otherwise reach PingAsync's callers as a
        // raw System.Text.Json.JsonException — exactly the tooling-layer leak this method's carefully
        // translated ToolNotFoundException/ToolFailedException branches above already exist to prevent.
        var runner = Installed().When(_ => true, output: ["{not valid json"]);

        await Assert.ThrowsAsync<EngineUnreachableException>(() => Engine(runner).PingAsync().AsTask());
    }

    [Fact]
    public async Task PingAsync_on_a_literal_null_line_throws_EngineUnreachableException_not_a_raw_NullReferenceException()
    {
        // The literal line "null" deserializes to a null NerdctlInfo; reading SecurityOptions off it
        // three lines later would otherwise be a raw NullReferenceException, not an engine-level failure.
        var runner = Installed().When(_ => true, output: ["null"]);

        await Assert.ThrowsAsync<EngineUnreachableException>(() => Engine(runner).PingAsync().AsTask());
    }

    [Fact]
    public async Task GetInfoAsync_reports_the_containerd_version_and_the_namespace_as_the_endpoint()
    {
        var runner = Installed().When(_ => true, output: [InfoFixture]);

        var info = await Engine(runner).GetInfoAsync();

        // "v2.3.1" here is containerd's own version, not nerdctl's — that is the whole point of the
        // fixture; asserting anything else would silently accept the mix-up the brief warns about.
        Assert.Equal("v2.3.1", info.Version);
        Assert.Equal("k8s.io", info.Endpoint);
        Assert.Equal("nerdctl:k8s.io", info.Backend);
        Assert.Equal(EngineConnectionState.Connected, info.ConnectionState);
    }

    [Fact]
    public async Task Rootless_is_false_against_the_real_capture_which_has_no_such_entry()
    {
        var runner = Installed().When(_ => true, output: [InfoFixture]);
        var engine = Engine(runner);

        await engine.GetInfoAsync();

        Assert.False(engine.Capabilities.Rootless);
    }

    [Fact]
    public async Task Rootless_flips_true_when_SecurityOptions_names_it()
    {
        var runner = Installed().When(_ => true, output: [RootlessInfoFixture]);
        var engine = Engine(runner);

        await engine.GetInfoAsync();

        Assert.True(engine.Capabilities.Rootless);
    }

    [Fact]
    public void Every_operation_this_backend_cannot_serve_throws_NotSupportedException()
    {
        // What is left here after PR 4 is no longer "not built yet" — each of these names a limitation
        // of nerdctl or of the tool seam, spelled out on the member itself. Everything implemented is
        // covered by its own file: reading (NerdctlEngineReadTests), lifecycle
        // (NerdctlEngineLifecycleTests), create (NerdctlEngineCreateTests), volumes and networks
        // (NerdctlEngineVolumeNetworkTests), prune (NerdctlEnginePruneTests), and this PR's streams,
        // build, Compose and image writes (NerdctlEngineAdvancedTests).
        //
        // BuildImageAsync, ComposeUpAsync, PullImageAsync, StreamStatsAsync and StreamEventsAsync are
        // deliberately absent from the array below even where they can still refuse: they are iterators,
        // so their exception surfaces on the first MoveNext rather than from the call expression, and
        // Assert.Throws here needs the latter. NerdctlEngineAdvancedTests asserts those by enumerating.
        var engine = Engine(new FakeToolRunner());

        // CA2012 wants every ValueTask awaited. These calls never produce one — each member throws
        // synchronously, which is precisely what is under test — so awaiting would only obscure that.
        // Scoped to this array rather than switched off for the repository's tests: elsewhere, an
        // unawaited ValueTask is the bug the rule is there to find.
#pragma warning disable CA2012
        Action[] calls =
        [
            () => _ = engine.ExecAsync("id", new ExecRequest { Command = ["echo"] }),
            () => _ = engine.StartExecSessionAsync("id", new ExecRequest { Command = ["echo"] }),
            () => _ = engine.VerifyRegistryLoginAsync(new RegistryCredential("host", "user", "secret")),
            () => _ = engine.InspectImageAsync("nginx"),
            () => _ = engine.BrowseVolumeAsync("v"),
            () => _ = engine.ConnectNetworkAsync("id", "n"),
            () => _ = engine.DisconnectNetworkAsync("id", "n"),
        ];
#pragma warning restore CA2012

        Assert.All(calls, call => Assert.Throws<NotSupportedException>(call));
    }
}
