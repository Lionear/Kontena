using Kontena.Sdk.Errors;
using Kontena.Sdk.Models;
using Kontena.Sdk.Tooling;
using Kontena.Sdk.Tooling.Fakes;

namespace Kontena.Plugins.Nerdctl.Tests;

/// <summary>
/// <see cref="NerdctlEngine"/>'s PR 4 payload: live stats and events, image build, Compose, and the
/// image write side. Every one of these drives a command whose exact argument list decides whether the
/// output is even parseable — <c>--no-stream</c> and <c>--progress=plain</c> are the difference between
/// JSON and a redrawing terminal display (Notes/nerdctl-advanced-formats.md) — so the command line is
/// asserted alongside the mapped result, not instead of it.
/// <para>
/// The buildkit socket root is pointed at a directory these tests control. The real one is a fixed host
/// path, and asserting against that would make <see cref="EngineCapabilities.SupportsBuild"/> depend on
/// whether the machine running the suite happens to have buildkit installed.
/// </para>
/// </summary>
public sealed class NerdctlEngineAdvancedTests
{
    private static readonly string InfoFixture = File.ReadAllText(Path.Combine("Fixtures", "info.json"));
    private static readonly string StatsFixture = File.ReadAllText(Path.Combine("Fixtures", "stats.json"));
    private static readonly string EventsFixture = File.ReadAllText(Path.Combine("Fixtures", "events.ndjson"));

    private static FakeToolRunner Installed() => new FakeToolRunner().Install(NerdctlTool.Definition);

    /// <summary>A directory that does not exist, so <c>DetectBuildkit</c> finds no socket.</summary>
    private static string NoBuildkit() =>
        Path.Combine(Path.GetTempPath(), $"kontena-nerdctl-{Guid.NewGuid():N}", "buildkit");

    private static NerdctlEngine Engine(
        IToolRunner runner, string @namespace = "k8s.io", string? buildkitRoot = null) =>
        new(new NerdctlCli(runner, @namespace), $"nerdctl:{@namespace}", $"nerdctl ({@namespace})",
            @namespace, buildkitRoot ?? NoBuildkit());

    /// <summary>
    /// Clears <c>BUILDKIT_HOST</c> for the duration of a test. nerdctl consults that variable before any
    /// socket path and <c>DetectBuildkit</c> takes it at its word, so a developer who has it set would
    /// otherwise see the "no buildkitd" tests fail for a reason that has nothing to do with the code.
    /// </summary>
    private sealed class WithoutBuildkitHost : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable("BUILDKIT_HOST");

        public WithoutBuildkitHost() => Environment.SetEnvironmentVariable("BUILDKIT_HOST", null);

        public void Dispose() => Environment.SetEnvironmentVariable("BUILDKIT_HOST", _previous);
    }

    // ── StreamStatsAsync ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StreamStatsAsync_asks_for_a_single_json_sample_not_the_redrawing_display()
    {
        var runner = Installed().When(_ => true, output: [StatsFixture]);

        await foreach (var _ in Engine(runner).StreamStatsAsync("statsprobe"))
            break;

        Assert.Equal(
            ["--namespace", "k8s.io", "stats", "--no-stream", "--format", "json", "statsprobe"],
            runner.Invocations[^1].Arguments);
    }

    [Fact]
    public async Task StreamStatsAsync_maps_the_binary_sizes_of_a_real_sample()
    {
        var runner = Installed().When(_ => true, output: [StatsFixture]);

        ContainerStats? sample = null;
        await foreach (var s in Engine(runner).StreamStatsAsync("statsprobe"))
        {
            sample = s;
            break;
        }

        Assert.NotNull(sample);
        Assert.Equal("statsprobe", sample.ContainerId);
        Assert.InRange(sample.MemoryUsedBytes, 13_000_000, 14_000_000);
        Assert.InRange(sample.MemoryLimitBytes, 60_000_000_000, 70_000_000_000);
        Assert.Equal(0, sample.CpuPercent);
    }

    [Fact]
    public async Task StreamStatsAsync_for_an_unknown_id_throws_ResourceNotFoundException()
    {
        var runner = Installed().When(_ => true, errorOutput: ["no such container: nope"], exitCode: 1);

        await Assert.ThrowsAsync<ResourceNotFoundException>(async () =>
        {
            await foreach (var _ in Engine(runner).StreamStatsAsync("nope"))
                break;
        });
    }

    [Fact]
    public async Task StreamStatsAsync_translates_a_missing_binary_into_the_shared_engine_exception()
    {
        await Assert.ThrowsAsync<EngineUnreachableException>(async () =>
        {
            await foreach (var _ in Engine(new FakeToolRunner()).StreamStatsAsync("id"))
                break;
        });
    }

    // ── StreamEventsAsync ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StreamEventsAsync_reads_the_records_of_a_real_capture_blank_lines_and_all()
    {
        // The fixture carries the blank line nerdctl prints between records — a naive reader treats it
        // as a record and dies on it.
        var runner = Installed().When(_ => true, output: EventsFixture.Split('\n'));

        var events = new List<EngineEvent>();
        await foreach (var e in Engine(runner).StreamEventsAsync())
            events.Add(e);

        Assert.Equal(2, events.Count);
        Assert.Equal(
            ["--namespace", "k8s.io", "events", "--format", "json"],
            runner.Invocations[^1].Arguments);
        Assert.Contains(events, e => e.Type == EngineEventType.Created && e.ResourceId == "62091b25…");
    }

    [Fact]
    public async Task StreamEventsAsync_skips_a_line_it_cannot_read_instead_of_ending_the_stream()
    {
        // An activity feed that stops on the first unfamiliar record silently hides every later event —
        // a worse failure than dropping the one line nobody could parse.
        var runner = Installed().When(_ => true, output:
        [
            "not json at all",
            """{"Timestamp":"2026-08-03T12:28:22.949015516Z","ID":"","Namespace":"default","Topic":"/tasks/start","Status":"unknown","Event":"{\"container_id\":\"abc\",\"id\":\"abc\"}"}""",
        ]);

        var events = new List<EngineEvent>();
        await foreach (var e in Engine(runner).StreamEventsAsync())
            events.Add(e);

        var only = Assert.Single(events);
        Assert.Equal(EngineEventType.Started, only.Type);
        Assert.Equal("abc", only.ResourceId);
    }

    [Fact]
    public async Task StreamEventsAsync_ignores_nerdctls_own_narration_on_stderr()
    {
        var runner = Installed().When(_ => true,
            output: [],
            errorOutput: ["""level=info msg="something nerdctl wants to say" """]);

        var events = new List<EngineEvent>();
        await foreach (var e in Engine(runner).StreamEventsAsync())
            events.Add(e);

        Assert.Empty(events);
    }

    // ── BuildImageAsync ─────────────────────────────────────────────────────────────────────────

    /// <summary>Creates a fake buildkitd socket where <c>DetectBuildkit</c> looks for the namespaced one.</summary>
    private static string BuildkitRootWithSocket(string @namespace)
    {
        var root = Path.Combine(Path.GetTempPath(), $"kontena-nerdctl-{Guid.NewGuid():N}", "buildkit");
        Directory.CreateDirectory($"{root}-{@namespace}");
        File.WriteAllText(Path.Combine($"{root}-{@namespace}", "buildkitd.sock"), string.Empty);
        return root;
    }

    [Fact]
    public async Task SupportsBuild_stays_false_until_a_buildkitd_socket_is_actually_found()
    {
        using var _ = new WithoutBuildkitHost();
        var runner = Installed().When(i => i.Arguments.Contains("info"), output: [InfoFixture]);
        var engine = Engine(runner);

        await engine.GetInfoAsync();

        Assert.False(engine.Capabilities.SupportsBuild);
    }

    [Fact]
    public async Task SupportsBuild_turns_on_once_the_namespaced_socket_exists()
    {
        using var _ = new WithoutBuildkitHost();
        var runner = Installed().When(i => i.Arguments.Contains("info"), output: [InfoFixture]);
        var engine = Engine(runner, buildkitRoot: BuildkitRootWithSocket("k8s.io"));

        // Before reading `info` nothing has been looked at, so the honest answer is still no.
        Assert.False(engine.Capabilities.SupportsBuild);

        await engine.GetInfoAsync();

        Assert.True(engine.Capabilities.SupportsBuild);
    }

    [Fact]
    public async Task BuildImageAsync_without_buildkitd_refuses_and_names_both_sockets_and_the_url()
    {
        using var _ = new WithoutBuildkitHost();
        var engine = Engine(Installed());

        var ex = await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var __ in engine.BuildImageAsync(new BuildRequest { ContextPath = ".", Tag = "x" }))
                break;
        });

        Assert.Contains("buildkitd.sock", ex.Message, StringComparison.Ordinal);
        Assert.Contains("-k8s.io/buildkitd.sock", ex.Message, StringComparison.Ordinal);
        Assert.Contains("https://github.com/moby/buildkit", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildImageAsync_always_asks_for_plain_progress_and_resolves_the_dockerfile_into_the_context()
    {
        using var _ = new WithoutBuildkitHost();
        var context = Directory.CreateTempSubdirectory("kontena-nerdctl-context").FullName;
        var runner = Installed().When(i => i.Arguments.Contains("info"), output: [InfoFixture]);
        var engine = Engine(runner, buildkitRoot: BuildkitRootWithSocket("k8s.io"));
        await engine.GetInfoAsync();

        var request = new BuildRequest
        {
            ContextPath = context,
            Tag = "probe:v1",
            Target = "runtime",
            NoCache = true,
            BuildArgs = new Dictionary<string, string> { ["VERSION"] = "1.2.3" },
        };

        await foreach (var __ in engine.BuildImageAsync(request))
        {
        }

        var args = runner.Invocations[^1].Arguments;

        Assert.Equal(["--namespace", "k8s.io", "build", "--progress=plain"], args.Take(4));
        // Without --progress=plain nerdctl's default output redraws in place, which read line by line is
        // not a build log at all.
        Assert.Contains("--progress=plain", args, StringComparer.Ordinal);
        Assert.Contains(Path.Combine(context, "Dockerfile"), args, StringComparer.Ordinal);
        Assert.Contains("--target", args, StringComparer.Ordinal);
        Assert.Contains("--no-cache", args, StringComparer.Ordinal);
        Assert.Contains("--pull", args, StringComparer.Ordinal);
        Assert.Contains("VERSION=1.2.3", args, StringComparer.Ordinal);
        Assert.Equal(context, args[^1]);

        Directory.Delete(context, recursive: true);
    }

    [Fact]
    public async Task BuildImageAsync_reports_a_missing_context_as_a_failed_line_not_an_exception()
    {
        using var _ = new WithoutBuildkitHost();
        var runner = Installed().When(i => i.Arguments.Contains("info"), output: [InfoFixture]);
        var engine = Engine(runner, buildkitRoot: BuildkitRootWithSocket("k8s.io"));
        await engine.GetInfoAsync();

        var progress = new List<BuildProgress>();
        await foreach (var p in engine.BuildImageAsync(
            new BuildRequest { ContextPath = Path.Combine(Path.GetTempPath(), "nope-not-here"), Tag = "x" }))
        {
            progress.Add(p);
        }

        var only = Assert.Single(progress);
        Assert.NotNull(only.Error);
        Assert.Contains("nope-not-here", only.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildImageAsync_passes_nerdctls_own_failure_through_as_the_error()
    {
        using var _ = new WithoutBuildkitHost();
        var context = Directory.CreateTempSubdirectory("kontena-nerdctl-context").FullName;
        var runner = Installed()
            .When(i => i.Arguments.Contains("info"), output: [InfoFixture])
            .When(i => i.Arguments.Contains("build"),
                output: ["#1 [internal] load build definition from Dockerfile"],
                errorOutput: ["""level=fatal msg="no buildkit host is available, tried 2 candidates" """],
                exitCode: 1);
        var engine = Engine(runner, buildkitRoot: BuildkitRootWithSocket("k8s.io"));
        await engine.GetInfoAsync();

        var progress = new List<BuildProgress>();
        await foreach (var p in engine.BuildImageAsync(new BuildRequest { ContextPath = context, Tag = "x" }))
            progress.Add(p);

        Assert.Contains(progress, p => p.Error is null && p.Text.StartsWith("#1 ", StringComparison.Ordinal));
        var failure = Assert.Single(progress, p => p.Error is not null);
        Assert.Contains("no buildkit host is available", failure.Error!, StringComparison.Ordinal);

        Directory.Delete(context, recursive: true);
    }

    // ── ComposeUpAsync ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ComposeUpAsync_builds_the_command_line_from_the_request()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("kontena-nerdctl-compose").FullName, "compose.yaml");
        await File.WriteAllTextAsync(file, "services: {}");
        var runner = Installed().When(_ => true, output: []);

        await foreach (var _ in Engine(runner).ComposeUpAsync(new ComposeUpRequest
        {
            ComposeFilePath = file,
            ProjectName = "cmp",
            Build = true,
            ForceRecreate = true,
        }))
        {
        }

        Assert.Equal(
            ["--namespace", "k8s.io", "compose", "-f", file, "-p", "cmp", "up", "-d", "--build", "--force-recreate"],
            runner.Invocations[^1].Arguments);

        File.Delete(file);
    }

    [Fact]
    public async Task ComposeUpAsync_unwraps_the_logrus_lines_nerdctl_narrates_with()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("kontena-nerdctl-compose").FullName, "compose.yaml");
        await File.WriteAllTextAsync(file, "services: {}");
        var runner = Installed().When(_ => true, errorOutput:
        [
            """level=info msg="Ensuring image docker.io/library/nginx:latest" """,
            """level=info msg="Creating container cmp-web-1" """,
        ]);

        var progress = new List<ComposeProgress>();
        await foreach (var p in Engine(runner).ComposeUpAsync(new ComposeUpRequest { ComposeFilePath = file }))
            progress.Add(p);

        Assert.Equal(2, progress.Count);
        Assert.Equal("Creating container cmp-web-1", progress[^1].Text);
        Assert.All(progress, p => Assert.Null(p.Error));

        File.Delete(file);
    }

    [Fact]
    public async Task ComposeUpAsync_marks_a_fatal_line_as_the_failure_it_is()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("kontena-nerdctl-compose").FullName, "compose.yaml");
        await File.WriteAllTextAsync(file, "services: {}");
        var runner = Installed().When(_ => true,
            errorOutput: ["""level=fatal msg="service web: image not found" """]);

        var progress = new List<ComposeProgress>();
        await foreach (var p in Engine(runner).ComposeUpAsync(new ComposeUpRequest { ComposeFilePath = file }))
            progress.Add(p);

        var failure = Assert.Single(progress);
        Assert.Equal("service web: image not found", failure.Error);

        File.Delete(file);
    }

    [Fact]
    public async Task ComposeUpAsync_answers_a_missing_file_itself_rather_than_letting_nerdctl_fail_on_it()
    {
        var runner = Installed();

        var progress = new List<ComposeProgress>();
        await foreach (var p in Engine(runner).ComposeUpAsync(
            new ComposeUpRequest { ComposeFilePath = "/no/such/compose.yaml" }))
        {
            progress.Add(p);
        }

        var only = Assert.Single(progress);
        Assert.Contains("/no/such/compose.yaml", only.Error!, StringComparison.Ordinal);
        Assert.Empty(runner.Invocations);
    }

    // ── Image writes ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PullImageAsync_streams_nerdctls_progress_lines_without_inventing_byte_counts()
    {
        var runner = Installed().When(_ => true, output:
        [
            "docker.io/library/nginx:latest: resolving",
            "Pulling from OCI Registry (docker.io/library/nginx:latest)\telapsed: 0.7 s\ttotal: 21.1 K",
        ]);

        var progress = new List<PullProgress>();
        await foreach (var p in Engine(runner).PullImageAsync("nginx:latest"))
            progress.Add(p);

        Assert.Equal(["--namespace", "k8s.io", "pull", "nginx:latest"], runner.Invocations[^1].Arguments);
        Assert.Equal(2, progress.Count);
        Assert.All(progress, p => Assert.Equal("nginx:latest", p.Reference));
        // nerdctl prints no per-layer counts a caller could total, so these stay null rather than
        // carrying a number scraped out of the text.
        Assert.All(progress, p => Assert.Null(p.Current));
        Assert.All(progress, p => Assert.Null(p.Total));
    }

    [Fact]
    public async Task PullImageAsync_refuses_a_credential_rather_than_pulling_unauthenticated()
    {
        var engine = Engine(Installed());

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in engine.PullImageAsync("nginx", new RegistryCredential("host", "u", "s")))
                break;
        });
    }

    [Fact]
    public async Task PullImageAsync_translates_a_failure_into_an_engine_exception()
    {
        var runner = Installed().When(_ => true, errorOutput: ["failed to resolve reference"], exitCode: 1);

        await Assert.ThrowsAsync<EngineException>(async () =>
        {
            await foreach (var _ in Engine(runner).PullImageAsync("nope"))
            {
            }
        });
    }

    [Fact]
    public async Task TagImageAsync_runs_tag_with_source_then_target()
    {
        var runner = Installed().When(_ => true, output: []);

        await Engine(runner).TagImageAsync("probe:v1", "probe:latest");

        Assert.Equal(
            ["--namespace", "k8s.io", "tag", "probe:v1", "probe:latest"],
            runner.Invocations[^1].Arguments);
    }

    [Fact]
    public async Task RemoveImageAsync_adds_dash_f_only_when_forced()
    {
        var runner = Installed().When(_ => true, output: ["Deleted: sha256:2df3df17"]);
        var engine = Engine(runner);

        await engine.RemoveImageAsync("probe:v1");
        Assert.Equal(["--namespace", "k8s.io", "rmi", "probe:v1"], runner.Invocations[^1].Arguments);

        await engine.RemoveImageAsync("probe:v1", force: true);
        Assert.Equal(["--namespace", "k8s.io", "rmi", "-f", "probe:v1"], runner.Invocations[^1].Arguments);
    }

    [Fact]
    public async Task RemoveImageAsync_surfaces_nerdctls_own_words_on_failure()
    {
        var runner = Installed().When(_ => true,
            errorOutput: ["image is being used by container abc"], exitCode: 1);

        var ex = await Assert.ThrowsAsync<EngineException>(
            () => Engine(runner).RemoveImageAsync("probe:v1").AsTask());

        Assert.Contains("being used by container", ex.Message, StringComparison.Ordinal);
    }

    // ── Capabilities ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_capabilities_this_PR_turns_on_are_on_and_the_permanent_gaps_stay_off()
    {
        var capabilities = Engine(Installed()).Capabilities;

        Assert.True(capabilities.SupportsStats);
        Assert.True(capabilities.SupportsEvents);
        Assert.True(capabilities.SupportsCompose);
        Assert.True(capabilities.SupportsPrune);

        // Not "not yet": no stdin/PTY in the tool seam, and no captured output for volume browsing.
        Assert.False(capabilities.SupportsExec);
        Assert.False(capabilities.SupportsVolumeBrowse);
    }
}
