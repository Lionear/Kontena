using System.Runtime.CompilerServices;
using Kontena.Sdk;
using Kontena.Sdk.Errors;
using Kontena.Sdk.Models;
using Kontena.Sdk.Shell;
using Kontena.Sdk.Tooling;

namespace Kontena.Adapters.Apple;

/// <summary>
/// CEAL over Apple's native macOS <c>container</c> runtime (KON-31). Each container is its own
/// lightweight VM and there is no daemon socket, so everything here comes from parsing what the CLI
/// prints — the formats were captured against a real install rather than read from its docs (Depot
/// kontena/Notes/apple-container-cli-formats.md).
/// <para>
/// Two kinds of <see cref="NotSupportedException"/> live in this file and they mean different things:
/// </para>
/// <list type="bullet">
/// <item><description><b>The runtime cannot do it.</b> <c>container</c> 1.2 has no pause, no Compose,
/// no event stream, and no way to attach a running container to a network — those subcommands do not
/// exist (<see cref="PauseUnsupported"/>, <see cref="ComposeUnsupported"/>,
/// <see cref="EventsUnsupported"/>, <see cref="NetworkAttachUnsupported"/>). Where a capability flag
/// exists for it, <see cref="Capabilities"/> already says so, and the UI does not offer it.</description></item>
/// <item><description><b>Not built yet.</b> Creating containers, images, builds, registry logins and
/// pruning land in the next stages of KON-31 (<see cref="NotYetBuilt"/>). No capability flag promises
/// any of them in the meantime, so nothing reachable in the UI arrives at one.</description></item>
/// </list>
/// </summary>
internal sealed class AppleEngine(AppleCli cli, string backend, string displayName) : IContainerEngine
{
    private const string PauseUnsupported =
        "Apple container cannot pause a container: each one is its own virtual machine and the CLI has " +
        "no pause/resume subcommand (verified against 1.2.2).";

    private const string ComposeUnsupported =
        "Apple container has no Compose support: the CLI has no compose subcommand (verified against 1.2.2).";

    private const string EventsUnsupported =
        "Apple container does not stream engine events: the CLI has no events subcommand (verified " +
        "against 1.2.2). Lists refresh on demand instead.";

    private const string NetworkAttachUnsupported =
        "Apple container attaches networks when a container is created, not afterwards: the CLI has no " +
        "network connect/disconnect subcommand (verified against 1.2.2).";

    private const string NotYetBuilt =
        "This part of the Apple container adapter is not built yet (KON-31).";

    /// <summary>
    /// How often <see cref="StreamStatsAsync"/> takes a sample. The same two seconds the nerdctl plugin
    /// uses: each sample is a process, and a graph that moves faster than a person reads costs more than
    /// it shows.
    /// </summary>
    private static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(2);

    public string Backend => backend;

    /// <summary>
    /// What this runtime can do. <see cref="EngineCapabilities.Rootless"/> is true and not a
    /// simplification: containers run in per-container VMs launched by a user-level launchd service, so
    /// there is no root daemon on the host to speak of — that is the runtime's whole design.
    /// <para>
    /// Build, exec, stats, prune and volume browsing are all present in the CLI but are reported as
    /// unsupported until their stage of KON-31 lands. A flag that promises what the adapter cannot yet
    /// do would put a live button in front of an exception, which is worse than a feature arriving one
    /// PR later.
    /// </para>
    /// </summary>
    public EngineCapabilities Capabilities => new()
    {
        Rootless = true,
        SupportsBuild = false,
        SupportsCompose = false,
        SupportsExec = true,
        SupportsPrune = false,
        SupportsVolumeBrowse = false,
        SupportsGpu = false,
        SupportsStats = true,
        SupportsEvents = false,
    };

    public async ValueTask<BackendInfo> GetInfoAsync(CancellationToken ct = default)
    {
        var versions = await cli
            .ListAsync<AppleVersion>(ct, "system", "version", "--format", "json")
            .ConfigureAwait(false);

        return new BackendInfo
        {
            Backend = backend,
            DisplayName = displayName,
            Kind = "container engine",
            Version = AppleMap.Version(versions),

            // There is no socket or URL to name: the CLI talks to a launchd-managed apiserver over XPC.
            // Saying so is more use in the title bar than an empty field or an invented path.
            Endpoint = "apiserver (XPC)",
            ConnectionState = EngineConnectionState.Connected,
        };
    }

    /// <summary>
    /// Asks the apiserver whether it is up. <c>system status</c> is the cheapest command that fails when
    /// the launchd service is not running — which, unlike a missing install, is an ordinary state here:
    /// <c>container system start</c> is a thing users do.
    /// </summary>
    public async ValueTask PingAsync(CancellationToken ct = default)
    {
        try
        {
            await cli.RunAsync(ct, "system", "status").ConfigureAwait(false);
        }
        catch (ToolNotFoundException error)
        {
            throw new EngineUnreachableException("Apple container is not installed.", error);
        }
    }

    // ── Containers ──────────────────────────────────────────────────────────

    public async ValueTask<IReadOnlyList<ContainerSummary>> ListContainersAsync(
        bool all = true, CancellationToken ct = default)
    {
        var containers = await ListRawContainersAsync(all, ct).ConfigureAwait(false);
        return [.. containers.Select(c => AppleMap.Container(c, backend))];
    }

    /// <summary>
    /// The raw list, shared by the container, volume and network listings: a volume's "used by" and a
    /// network's "attached containers" are not fields this CLI prints, they are answered by looking at
    /// what the containers mount and join.
    /// </summary>
    private ValueTask<IReadOnlyList<AppleContainer>> ListRawContainersAsync(bool all, CancellationToken ct) =>
        all
            ? cli.ListAsync<AppleContainer>(ct, "list", "--all", "--format", "json")
            : cli.ListAsync<AppleContainer>(ct, "list", "--format", "json");

    /// <summary>
    /// <c>inspect</c> prints the same record the list does, filtered by id — so this is one call, and the
    /// mapping reads more fields off the same shape rather than a second model.
    /// </summary>
    public async ValueTask<ContainerInspect> InspectContainerAsync(string id, CancellationToken ct = default)
    {
        var containers = await cli.ListAsync<AppleContainer>(ct, "inspect", id).ConfigureAwait(false);

        // The CLI errors on an unknown id, so an empty array means it answered about nothing at all —
        // reported as the same missing-resource condition rather than returning a blank detail page.
        return containers.Count > 0
            ? AppleMap.Inspect(containers[0])
            : throw new ResourceNotFoundException($"Container not found: {id}");
    }

    public async ValueTask StartContainerAsync(string id, CancellationToken ct = default) =>
        await cli.RunAsync(ct, "start", id).ConfigureAwait(false);

    public async ValueTask StopContainerAsync(string id, CancellationToken ct = default) =>
        await cli.RunAsync(ct, "stop", id).ConfigureAwait(false);

    /// <summary>
    /// Stop, then start. <c>container</c> has no <c>restart</c> subcommand, and doing it in two steps
    /// here rather than leaving the button unimplemented keeps the restart honest: if the stop fails,
    /// nothing is started and the caller sees why.
    /// </summary>
    public async ValueTask RestartContainerAsync(string id, CancellationToken ct = default)
    {
        await StopContainerAsync(id, ct).ConfigureAwait(false);
        await StartContainerAsync(id, ct).ConfigureAwait(false);
    }

    public async ValueTask RemoveContainerAsync(
        string id, bool force = false, CancellationToken ct = default)
    {
        if (force)
            await cli.RunAsync(ct, "delete", "--force", id).ConfigureAwait(false);
        else
            await cli.RunAsync(ct, "delete", id).ConfigureAwait(false);
    }

    /// <summary>Not available — see <see cref="PauseUnsupported"/>.</summary>
    public ValueTask PauseContainerAsync(string id, CancellationToken ct = default) =>
        throw new NotSupportedException(PauseUnsupported);

    /// <summary>Not available — see <see cref="PauseUnsupported"/>.</summary>
    public ValueTask UnpauseContainerAsync(string id, CancellationToken ct = default) =>
        throw new NotSupportedException(PauseUnsupported);

    /// <summary>Creating containers lands with the Run flow, in the next stage of KON-31.</summary>
    public ValueTask<string> CreateContainerAsync(
        CreateContainerRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException(NotYetBuilt);

    /// <summary>
    /// Runs a command and hands back its exit code. <c>container exec</c> exits with the code of the
    /// process it ran, so unlike nerdctl there is nothing to dig out of an error line — but it also
    /// means a refusal has to be told apart from a command that simply failed, which
    /// <see cref="AppleCli.RunForExitCodeAsync"/> does.
    /// </summary>
    public ValueTask<int> ExecAsync(string id, ExecRequest request, CancellationToken ct = default) =>
        cli.RunForExitCodeAsync(ct, [.. ExecArguments(id, request, interactive: false)]);

    /// <summary>
    /// Opens an interactive shell in the container, in a real pseudo-terminal.
    /// <para>
    /// Not through <see cref="IToolRunner"/>: that seam starts a process and reads its output, with no
    /// way to write to its stdin and no PTY — which is exactly why the nerdctl plugin has to refuse
    /// this. <see cref="PtyShellSession"/> is the seam that does have one, and the command it starts is
    /// an ordinary <c>container exec -i -t</c>.
    /// </para>
    /// <para>
    /// The binary is resolved rather than named: the pseudo-terminal spawns it directly, so it must get
    /// the path the tool runner would have used instead of trusting whatever PATH the app inherited.
    /// </para>
    /// </summary>
    public async ValueTask<IExecSession> StartExecSessionAsync(
        string id, ExecRequest request, CancellationToken ct = default)
    {
        var executable = await cli.LocateAsync(ct).ConfigureAwait(false);
        var command = new PtyCommand(executable, ExecArguments(id, request, interactive: true));

        // 80x24 is where every terminal starts; the view resizes it as soon as it has a size of its own.
        // The working directory is the host's, and irrelevant: -w decides where the process runs inside
        // the container.
        return await PtyShellSession
            .StartAsync(command, Path.GetTempPath(), columns: 80, rows: 24, supportDirectory: null, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the argument list both exec paths share. <c>-i</c> and <c>-t</c> only go on the
    /// interactive one: a one-shot exec that asked for a TTY would get its output line-buffered through
    /// a terminal and its stderr folded into stdout, for a caller that only wants an exit code.
    /// </summary>
    private static List<string> ExecArguments(string id, ExecRequest request, bool interactive)
    {
        var arguments = new List<string> { "exec" };

        if (interactive)
        {
            arguments.Add("--interactive");

            // The request's own Tty flag is honoured, but an interactive session without one is a shell
            // with no prompt and no line editing — so this asks for it whenever the caller did.
            if (request.Tty)
                arguments.Add("--tty");
        }

        if (request.WorkingDirectory is { Length: > 0 } directory)
        {
            arguments.Add("--workdir");
            arguments.Add(directory);
        }

        arguments.Add(id);
        arguments.AddRange(request.Command);

        return arguments;
    }

    /// <summary>
    /// Pruning lands in a later stage of KON-31. <c>container prune</c> reports what it reclaimed as a
    /// localised sentence ("Reclaimed 1,37 GB in disk space" — note the decimal comma), so the byte
    /// figure this returns has to come from <c>system df</c> rather than from parsing that line.
    /// </summary>
    public ValueTask<PruneResult> PruneContainersAsync(CancellationToken ct = default) =>
        throw new NotSupportedException(NotYetBuilt);

    // ── Images ──────────────────────────────────────────────────────────────

    public async ValueTask<IReadOnlyList<ImageSummary>> ListImagesAsync(CancellationToken ct = default)
    {
        var images = await cli
            .ListAsync<AppleImage>(ct, "image", "list", "--format", "json")
            .ConfigureAwait(false);

        // "In use" is not a field this CLI prints either; it is whether a container was created from
        // that reference, which the container list answers.
        var containers = await ListRawContainersAsync(all: true, ct).ConfigureAwait(false);
        var inUse = containers
            .Select(c => c.Configuration?.Image?.Reference)
            .Where(reference => !string.IsNullOrEmpty(reference))
            .ToHashSet(StringComparer.Ordinal)!;

        return [.. images.Select(image => AppleMap.Image(image, inUse!))];
    }

    /// <summary>Image pull, push, tag, removal and builds land in a later stage of KON-31.</summary>
    public IAsyncEnumerable<PullProgress> PullImageAsync(
        string reference, RegistryCredential? credential = null, CancellationToken ct = default) =>
        throw new NotSupportedException(NotYetBuilt);

    /// <summary>Registry logins land with image pulling, in a later stage of KON-31.</summary>
    public ValueTask VerifyRegistryLoginAsync(
        RegistryCredential credential, CancellationToken ct = default) =>
        throw new NotSupportedException(NotYetBuilt);

    /// <summary>Builds land in a later stage of KON-31; the CLI has its own BuildKit builder.</summary>
    public IAsyncEnumerable<BuildProgress> BuildImageAsync(
        BuildRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException(NotYetBuilt);

    /// <summary>Image removal lands in a later stage of KON-31.</summary>
    public ValueTask RemoveImageAsync(string id, bool force = false, CancellationToken ct = default) =>
        throw new NotSupportedException(NotYetBuilt);

    /// <summary>Reading an image's baked-in config lands with the Run flow, in a later stage of KON-31.</summary>
    public ValueTask<ImageConfig?> InspectImageAsync(string reference, CancellationToken ct = default) =>
        throw new NotSupportedException(NotYetBuilt);

    /// <summary>Tagging lands in a later stage of KON-31.</summary>
    public ValueTask TagImageAsync(string id, string newTag, CancellationToken ct = default) =>
        throw new NotSupportedException(NotYetBuilt);

    /// <summary>Pruning lands in a later stage of KON-31 — see <see cref="PruneContainersAsync"/>.</summary>
    public ValueTask<PruneResult> PruneImagesAsync(
        bool allUnused = true, CancellationToken ct = default) =>
        throw new NotSupportedException(NotYetBuilt);

    // ── Volumes ─────────────────────────────────────────────────────────────

    public async ValueTask<IReadOnlyList<VolumeSummary>> ListVolumesAsync(CancellationToken ct = default)
    {
        var volumes = await cli
            .ListAsync<AppleVolume>(ct, "volume", "list", "--format", "json")
            .ConfigureAwait(false);

        var containers = await ListRawContainersAsync(all: true, ct).ConfigureAwait(false);

        return [.. volumes.Select(volume => AppleMap.Volume(volume, containers))];
    }

    /// <summary>Creating and removing volumes lands in a later stage of KON-31.</summary>
    public ValueTask<VolumeSummary> CreateVolumeAsync(
        CreateVolumeRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException(NotYetBuilt);

    /// <summary>Creating and removing volumes lands in a later stage of KON-31.</summary>
    public ValueTask RemoveVolumeAsync(string name, bool force = false, CancellationToken ct = default) =>
        throw new NotSupportedException(NotYetBuilt);

    /// <summary>Volume browsing lands in a later stage of KON-31.</summary>
    public ValueTask<VolumeListing> BrowseVolumeAsync(
        string name, string path = "/", CancellationToken ct = default) =>
        throw new NotSupportedException(NotYetBuilt);

    /// <summary>Pruning lands in a later stage of KON-31 — see <see cref="PruneContainersAsync"/>.</summary>
    public ValueTask<PruneResult> PruneVolumesAsync(CancellationToken ct = default) =>
        throw new NotSupportedException(NotYetBuilt);

    // ── Networks ────────────────────────────────────────────────────────────

    public async ValueTask<IReadOnlyList<NetworkSummary>> ListNetworksAsync(CancellationToken ct = default)
    {
        var networks = await cli
            .ListAsync<AppleNetwork>(ct, "network", "list", "--format", "json")
            .ConfigureAwait(false);

        var containers = await ListRawContainersAsync(all: true, ct).ConfigureAwait(false);

        return [.. networks.Select(network => AppleMap.Network(network, containers))];
    }

    /// <summary>Creating and removing networks lands in a later stage of KON-31.</summary>
    public ValueTask<NetworkSummary> CreateNetworkAsync(
        CreateNetworkRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException(NotYetBuilt);

    /// <summary>Creating and removing networks lands in a later stage of KON-31.</summary>
    public ValueTask RemoveNetworkAsync(string id, CancellationToken ct = default) =>
        throw new NotSupportedException(NotYetBuilt);

    /// <summary>Not available — see <see cref="NetworkAttachUnsupported"/>.</summary>
    public ValueTask ConnectNetworkAsync(
        string containerId, string networkId, CancellationToken ct = default) =>
        throw new NotSupportedException(NetworkAttachUnsupported);

    /// <summary>Not available — see <see cref="NetworkAttachUnsupported"/>.</summary>
    public ValueTask DisconnectNetworkAsync(
        string containerId, string networkId, bool force = false, CancellationToken ct = default) =>
        throw new NotSupportedException(NetworkAttachUnsupported);

    // ── Compose ─────────────────────────────────────────────────────────────

    /// <summary>Not available — see <see cref="ComposeUnsupported"/>.</summary>
    public IAsyncEnumerable<ComposeProgress> ComposeUpAsync(
        ComposeUpRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException(ComposeUnsupported);

    // ── Streams ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Streams a container's log.
    /// <para>
    /// Everything arrives on one channel: <c>container logs</c> writes the container's stderr to its own
    /// stdout, so there is no split to map and every entry is reported as
    /// <see cref="LogSource.Stdout"/>. Claiming otherwise would put a colour on a line that means
    /// nothing.
    /// </para>
    /// <para>
    /// There is no <c>--timestamps</c> flag either, so the time on an entry is when Kontena read the
    /// line. <see cref="LogLine.Parse"/> still takes a stamp the container printed itself, which is the
    /// only case where the time is the container's own.
    /// </para>
    /// </summary>
    public async IAsyncEnumerable<LogEntry> StreamLogsAsync(
        string id, bool follow = true, [EnumeratorCancellation] CancellationToken ct = default)
    {
        string[] arguments = follow ? ["logs", "--follow", id] : ["logs", id];

        await foreach (var line in cli.StreamAsync(ct, arguments).ConfigureAwait(false))
            yield return LogLine.Parse(line.Text, LogSource.Stdout, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Samples a container's resource usage.
    /// <para>
    /// Polled, because there is nothing to stream: <c>container stats --format json</c> prints one array
    /// and exits — the continuously updating display the CLI is documented to have is the table format
    /// only. So this runs the snapshot on a timer, at the same two-second cadence the nerdctl plugin
    /// uses.
    /// </para>
    /// <para>
    /// The CPU figure is computed here rather than read: this CLI reports <c>cpuUsageUsec</c>, a counter
    /// that only goes up, where Docker and nerdctl hand over a percentage. It is the rise in that
    /// counter over the wall-clock time between two samples, which is why <b>the first sample reports
    /// zero</b> — there is no earlier one to subtract, and inventing a number for it would be a spike on
    /// every graph the moment it opens.
    /// </para>
    /// </summary>
    public async IAsyncEnumerable<ContainerStats> StreamStatsAsync(
        string id, [EnumeratorCancellation] CancellationToken ct = default)
    {
        AppleStats? previous = null;
        var previousAt = DateTimeOffset.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            var samples = await cli
                .ListAsync<AppleStats>(ct, "stats", "--format", "json", "--no-stream", id)
                .ConfigureAwait(false);

            // A container that stopped between two samples prints nothing rather than erroring. That is
            // the end of the stream, not a gap in it.
            if (samples.Count == 0)
                yield break;

            var current = samples[0];
            var now = DateTimeOffset.UtcNow;

            yield return AppleMap.Stats(current, previous, now - previousAt, id);

            previous = current;
            previousAt = now;

            try
            {
                await Task.Delay(StatsInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    /// <summary>Not available — see <see cref="EventsUnsupported"/>.</summary>
    public IAsyncEnumerable<EngineEvent> StreamEventsAsync(CancellationToken ct = default) =>
        throw new NotSupportedException(EventsUnsupported);
}
