using System.Runtime.CompilerServices;
using Kontena.Sdk;
using Kontena.Sdk.Errors;
using Kontena.Sdk.Models;
using Kontena.Sdk.Tooling;

namespace Kontena.Plugins.Nerdctl;

/// <summary>
/// CEAL implementation backed by the nerdctl CLI (KON-141) — one instance per containerd namespace,
/// matching the one-provider-per-namespace shape <see cref="NerdctlEngineProvider"/> already exposes.
/// <para>
/// This PR (nerdctl PR 2) gives the backend identity, reachability, honest capabilities (KON-141 task
/// 5) and reading containers/images/volumes/networks/inspect/logs (task 6) — every one of those against
/// the CLI shapes captured in Notes/nerdctl-cli-formats.md, never against nerdctl's documentation.
/// Every other member still throws <see cref="NotSupportedException"/> naming the PR that fills it in:
/// writing lands in PR 3, build/compose/exec/stats/events/volume-browsing in PR 4. That is acceptable
/// only because the plugin is not distributed until PR 5 — no user can reach any of this yet.
/// </para>
/// </summary>
public sealed class NerdctlEngine : IContainerEngine
{
    private const string WriteNotYet =
        "Creating, starting, stopping, removing and pruning containers/images/volumes/networks lands in nerdctl PR 3 (KON-141).";

    private const string ImageInspectNotYet =
        "Inspecting a single image is not part of nerdctl PR 2 task 6 — it lands alongside build/exec in nerdctl PR 4 (KON-141).";

    private const string AdvancedNotYet =
        "Build, compose, exec, stats, events and volume browsing land in nerdctl PR 4 (KON-141).";

    private readonly NerdctlCli _cli;
    private readonly string _backend;
    private readonly string _displayName;
    private readonly string _namespace;

    /// <summary>
    /// Whether the last successful <c>info</c> read named <c>name=rootless</c> in its security options.
    /// Starts false — the honest answer before anything has been observed — and is refreshed every
    /// <see cref="GetInfoAsync"/> call, since <see cref="Capabilities"/> has no async path of its own to
    /// read <c>info</c> through.
    /// </summary>
    private bool _rootless;

    public NerdctlEngine(NerdctlCli cli, string backend, string displayName, string @namespace)
    {
        _cli = cli;
        _backend = backend;
        _displayName = displayName;
        _namespace = @namespace;
    }

    public string Backend => _backend;

    /// <summary>
    /// Every capability but <see cref="EngineCapabilities.Rootless"/> is pinned false in this PR: none
    /// of the methods behind them work yet, and a capability that says yes while the method throws is
    /// worse than one that says no — the UI would offer a button that fails. Rootless is the one
    /// exception, because it is an observation read off <c>info</c>, not a promise about a method.
    /// </summary>
    public EngineCapabilities Capabilities => new()
    {
        Rootless = _rootless,
        SupportsBuild = false,
        SupportsCompose = false,
        SupportsExec = false,
        SupportsPrune = false,
        SupportsGpu = false,
        SupportsStats = false,
        SupportsEvents = false,
        SupportsVolumeBrowse = false,
    };

    public ValueTask PingAsync(CancellationToken ct = default) =>
        new(ReadInfoAsync(ct).AsTask());

    public async ValueTask<BackendInfo> GetInfoAsync(CancellationToken ct = default)
    {
        var info = await ReadInfoAsync(ct).ConfigureAwait(false);
        return new BackendInfo
        {
            Backend = _backend,
            DisplayName = _displayName,
            Kind = "container engine",
            // `ServerVersion` in nerdctl's `info` is containerd's own version (e.g. "v2.3.1"), never
            // nerdctl's — nerdctl does not report its own version in this payload at all.
            Version = info.ServerVersion,
            // The namespace, not a socket path — that is what tells one nerdctl backend apart from
            // another in the switcher, where Docker would show its socket.
            Endpoint = _namespace,
            ConnectionState = EngineConnectionState.Connected,
        };
    }

    /// <summary>
    /// Runs <c>nerdctl --namespace &lt;ns&gt; info --format json</c> — the one call both
    /// <see cref="PingAsync"/> and <see cref="GetInfoAsync"/> need — and refreshes
    /// <see cref="_rootless"/> from it. A missing binary or a non-zero exit both mean the same thing to
    /// a caller: this backend cannot be reached, so both become <see cref="EngineUnreachableException"/>,
    /// the same exception the other adapters raise for a stopped engine, so
    /// <c>BackendRegistry.ProbeAsync</c> counts this backend as "Not connected" the same way.
    /// </summary>
    private async ValueTask<NerdctlInfo> ReadInfoAsync(CancellationToken ct)
    {
        string stdout;
        try
        {
            stdout = await _cli.RunAsync(ct, "info", "--format", "json").ConfigureAwait(false);
        }
        catch (ToolNotFoundException ex)
        {
            throw new EngineUnreachableException($"nerdctl is not installed — cannot reach '{_backend}'.", ex);
        }
        catch (ToolFailedException ex)
        {
            throw new EngineUnreachableException($"nerdctl did not respond for '{_backend}': {ex.Message}", ex);
        }

        var rows = NerdctlJson.Parse<NerdctlInfo>(stdout);
        if (rows.Count == 0)
            throw new EngineUnreachableException($"'nerdctl info' returned nothing for '{_backend}'.");
        var info = rows[0];

        // There is no `Rootless` field in `info` at all — the trap this plugin's fixtures were captured
        // specifically to catch. Rootless is signalled the same way Docker signals it: a
        // "name=rootless" entry in SecurityOptions, nothing else.
        _rootless = info.SecurityOptions.Contains("name=rootless", StringComparer.Ordinal);

        return info;
    }

    /// <summary>
    /// Runs a listing command (<c>ps</c>, <c>images</c>, <c>network ls</c>, <c>volume ls</c>) and
    /// translates the two ways nerdctl can fail to the exceptions the rest of the CEAL already expects —
    /// the same translation <see cref="ReadInfoAsync"/> applies to <c>info</c>, so a caller sees one
    /// consistent pair of failures from this engine rather than a tooling exception for some commands
    /// and an engine exception for others.
    /// </summary>
    private async ValueTask<string> RunListAsync(CancellationToken ct, params string[] args)
    {
        try
        {
            return await _cli.RunAsync(ct, args).ConfigureAwait(false);
        }
        catch (ToolNotFoundException ex)
        {
            throw new EngineUnreachableException($"nerdctl is not installed — cannot reach '{_backend}'.", ex);
        }
        catch (ToolFailedException ex)
        {
            throw new EngineException($"nerdctl failed for '{_backend}': {ex.Message}", ex);
        }
    }

    // ── Containers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Runs <c>ps --format json</c>, adding <c>-a</c> only when <paramref name="all"/> is true. Getting
    /// this backwards is silent either way: without <c>-a</c>, a stopped container simply is not in the
    /// output — nerdctl does not say it hid anything — and with an unwanted <c>-a</c>, stopped
    /// containers appear as if they were live inventory.
    /// </summary>
    public async ValueTask<IReadOnlyList<ContainerSummary>> ListContainersAsync(
        bool all = true, CancellationToken ct = default)
    {
        var stdout = all
            ? await RunListAsync(ct, "ps", "-a", "--format", "json").ConfigureAwait(false)
            : await RunListAsync(ct, "ps", "--format", "json").ConfigureAwait(false);

        return [.. NerdctlJson.Parse<NerdctlContainer>(stdout).Select(c => c.ToSummary(_backend))];
    }

    public ValueTask<string> CreateContainerAsync(
        CreateContainerRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException(WriteNotYet);

    public ValueTask StartContainerAsync(string id, CancellationToken ct = default) =>
        throw new NotSupportedException(WriteNotYet);

    public ValueTask StopContainerAsync(string id, CancellationToken ct = default) =>
        throw new NotSupportedException(WriteNotYet);

    public ValueTask RestartContainerAsync(string id, CancellationToken ct = default) =>
        throw new NotSupportedException(WriteNotYet);

    public ValueTask PauseContainerAsync(string id, CancellationToken ct = default) =>
        throw new NotSupportedException(WriteNotYet);

    public ValueTask UnpauseContainerAsync(string id, CancellationToken ct = default) =>
        throw new NotSupportedException(WriteNotYet);

    public ValueTask RemoveContainerAsync(string id, bool force = false, CancellationToken ct = default) =>
        throw new NotSupportedException(WriteNotYet);

    /// <summary>
    /// Runs <c>nerdctl inspect &lt;id&gt;</c> — Docker-compatible, a JSON array with one element (see
    /// <see cref="NerdctlJson.ParseArray{T}"/>). An id nerdctl does not know about makes the CLI exit
    /// non-zero, surfaced by <see cref="NerdctlCli"/> as <see cref="ToolFailedException"/>; that is a
    /// tooling-layer exception naming a raw command line, not something the CEAL boundary should let
    /// through, so it becomes the same <see cref="ResourceNotFoundException"/> every other adapter
    /// raises for an unknown id.
    /// </summary>
    public async ValueTask<ContainerInspect> InspectContainerAsync(string id, CancellationToken ct = default)
    {
        string stdout;
        try
        {
            stdout = await _cli.RunAsync(ct, "inspect", id).ConfigureAwait(false);
        }
        catch (ToolNotFoundException ex)
        {
            throw new EngineUnreachableException($"nerdctl is not installed — cannot reach '{_backend}'.", ex);
        }
        catch (ToolFailedException ex)
        {
            throw new ResourceNotFoundException($"Container '{id}' was not found on '{_backend}'.", ex);
        }

        var rows = NerdctlJson.ParseArray<NerdctlInspectContainer>(stdout);
        if (rows.Count == 0)
            throw new ResourceNotFoundException($"Container '{id}' was not found on '{_backend}'.");

        return rows[0].ToInspect();
    }

    public ValueTask<int> ExecAsync(string id, ExecRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException(AdvancedNotYet);

    public ValueTask<IExecSession> StartExecSessionAsync(
        string id, ExecRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException(AdvancedNotYet);

    public ValueTask<PruneResult> PruneContainersAsync(CancellationToken ct = default) =>
        throw new NotSupportedException(WriteNotYet);

    // ── Images ──────────────────────────────────────────────────────────────

    public async ValueTask<IReadOnlyList<ImageSummary>> ListImagesAsync(CancellationToken ct = default)
    {
        var stdout = await RunListAsync(ct, "images", "--format", "json").ConfigureAwait(false);
        return [.. NerdctlJson.Parse<NerdctlImage>(stdout).Select(i => i.ToImage())];
    }

    public IAsyncEnumerable<PullProgress> PullImageAsync(
        string reference, RegistryCredential? credential = null, CancellationToken ct = default) =>
        throw new NotSupportedException(WriteNotYet);

    public ValueTask VerifyRegistryLoginAsync(RegistryCredential credential, CancellationToken ct = default) =>
        throw new NotSupportedException(WriteNotYet);

    public IAsyncEnumerable<BuildProgress> BuildImageAsync(
        BuildRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException(AdvancedNotYet);

    public ValueTask RemoveImageAsync(string id, bool force = false, CancellationToken ct = default) =>
        throw new NotSupportedException(WriteNotYet);

    public ValueTask<ImageConfig?> InspectImageAsync(string reference, CancellationToken ct = default) =>
        throw new NotSupportedException(ImageInspectNotYet);

    public ValueTask TagImageAsync(string id, string newTag, CancellationToken ct = default) =>
        throw new NotSupportedException(WriteNotYet);

    public ValueTask<PruneResult> PruneImagesAsync(bool allUnused = true, CancellationToken ct = default) =>
        throw new NotSupportedException(WriteNotYet);

    // ── Volumes ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs <c>volume ls --format json</c>. On a machine with no volumes — the ordinary state — nerdctl
    /// prints nothing at all: not <c>[]</c>, not a blank line. <see cref="NerdctlJson.Lines"/> already
    /// treats zero bytes as zero rows, so that ordinary case reaches here as an empty list rather than
    /// as something this method needs to special-case or that could throw.
    /// </summary>
    public async ValueTask<IReadOnlyList<VolumeSummary>> ListVolumesAsync(CancellationToken ct = default)
    {
        var stdout = await RunListAsync(ct, "volume", "ls", "--format", "json").ConfigureAwait(false);
        return [.. NerdctlJson.Parse<NerdctlVolume>(stdout).Select(v => v.ToVolume())];
    }

    public ValueTask<VolumeSummary> CreateVolumeAsync(
        CreateVolumeRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException(WriteNotYet);

    public ValueTask RemoveVolumeAsync(string name, bool force = false, CancellationToken ct = default) =>
        throw new NotSupportedException(WriteNotYet);

    public ValueTask<VolumeListing> BrowseVolumeAsync(
        string name, string path = "/", CancellationToken ct = default) =>
        throw new NotSupportedException(AdvancedNotYet);

    public ValueTask<PruneResult> PruneVolumesAsync(CancellationToken ct = default) =>
        throw new NotSupportedException(WriteNotYet);

    // ── Networks ────────────────────────────────────────────────────────────

    public async ValueTask<IReadOnlyList<NetworkSummary>> ListNetworksAsync(CancellationToken ct = default)
    {
        var stdout = await RunListAsync(ct, "network", "ls", "--format", "json").ConfigureAwait(false);
        return [.. NerdctlJson.Parse<NerdctlNetwork>(stdout).Select(n => n.ToNetwork())];
    }

    public ValueTask<NetworkSummary> CreateNetworkAsync(
        CreateNetworkRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException(WriteNotYet);

    public ValueTask RemoveNetworkAsync(string id, CancellationToken ct = default) =>
        throw new NotSupportedException(WriteNotYet);

    public ValueTask ConnectNetworkAsync(
        string containerId, string networkId, CancellationToken ct = default) =>
        throw new NotSupportedException(WriteNotYet);

    public ValueTask DisconnectNetworkAsync(
        string containerId, string networkId, bool force = false, CancellationToken ct = default) =>
        throw new NotSupportedException(WriteNotYet);

    // ── Compose ─────────────────────────────────────────────────────────────

    public IAsyncEnumerable<ComposeProgress> ComposeUpAsync(
        ComposeUpRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException(AdvancedNotYet);

    // ── Streams ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Streams <c>nerdctl logs &lt;id&gt;</c>, bare lines with no wrapper. <paramref name="follow"/>
    /// decides whether <c>-f</c> goes on the command line at all — without it, a "follow" reader would
    /// hang forever on a container that finished producing output.
    /// <para>
    /// Always asks for <c>--timestamps</c> rather than leaving <see cref="LogEntry.Timestamp"/> at
    /// <c>default</c>: nerdctl only stamps a line when asked, and <see cref="LogLine.Parse"/> already
    /// exists to read that stamp back off the line (the same helper <c>DockerEngine</c> uses), so asking
    /// for it costs nothing extra here. The alternative — inventing a timestamp from
    /// <see cref="DateTimeOffset.UtcNow"/> at read time — is exactly the mistake KON-203 fixed for
    /// Docker: a backlog of old lines all read in one burst would each get today's clock reading, which
    /// is a wrong answer presented as fact, not a missing one. <c>UtcNow</c> is passed to
    /// <see cref="LogLine.Parse"/> only as its fallback for the rare line nerdctl did not actually stamp.
    /// </para>
    /// </summary>
    public async IAsyncEnumerable<LogEntry> StreamLogsAsync(
        string id, bool follow = true, [EnumeratorCancellation] CancellationToken ct = default)
    {
        List<string> args = ["logs", "--timestamps"];
        if (follow)
            args.Add("-f");
        args.Add(id);

        var lines = _cli.StreamAsync(ct, [.. args]).GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                bool has;
                try
                {
                    has = await lines.MoveNextAsync().ConfigureAwait(false);
                }
                catch (ToolNotFoundException ex)
                {
                    throw new EngineUnreachableException(
                        $"nerdctl is not installed — cannot reach '{_backend}'.", ex);
                }
                catch (ToolFailedException ex)
                {
                    // Same reasoning as InspectContainerAsync: an id nerdctl does not know about is by
                    // far the most common reason `logs` exits non-zero, so the CLI-specific exception
                    // does not leak past the CEAL boundary either.
                    throw new ResourceNotFoundException($"Container '{id}' was not found on '{_backend}'.", ex);
                }

                if (!has)
                    yield break;

                var line = lines.Current;
                var source = line.Stream == ToolOutputKind.Error ? LogSource.Stderr : LogSource.Stdout;
                yield return LogLine.Parse(line.Text, source, DateTimeOffset.UtcNow);
            }
        }
        finally
        {
            await lines.DisposeAsync().ConfigureAwait(false);
        }
    }

    public IAsyncEnumerable<ContainerStats> StreamStatsAsync(string id, CancellationToken ct = default) =>
        throw new NotSupportedException(AdvancedNotYet);

    public IAsyncEnumerable<EngineEvent> StreamEventsAsync(CancellationToken ct = default) =>
        throw new NotSupportedException(AdvancedNotYet);
}
