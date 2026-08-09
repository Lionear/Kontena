using Kontena.Sdk;
using Kontena.Sdk.Errors;
using Kontena.Sdk.Models;
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
/// <item><description><b>Not built yet.</b> Streaming, exec, images and builds land in the next stages
/// of KON-31 (<see cref="NotYetBuilt"/>). These are reachable only from views this adapter's backend
/// does not yet drive, and each names what it is waiting for.</description></item>
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
        SupportsExec = false,
        SupportsPrune = false,
        SupportsVolumeBrowse = false,
        SupportsGpu = false,
        SupportsStats = false,
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

    /// <summary>Exec lands with the terminal, in a later stage of KON-31.</summary>
    public ValueTask<int> ExecAsync(string id, ExecRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException(NotYetBuilt);

    /// <summary>Exec lands with the terminal, in a later stage of KON-31.</summary>
    public ValueTask<IExecSession> StartExecSessionAsync(
        string id, ExecRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException(NotYetBuilt);

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

    /// <summary>Log streaming lands in the next stage of KON-31.</summary>
    public IAsyncEnumerable<LogEntry> StreamLogsAsync(
        string id, bool follow = true, CancellationToken ct = default) =>
        throw new NotSupportedException(NotYetBuilt);

    /// <summary>
    /// Stats land in a later stage of KON-31. The CLI reports cumulative counters
    /// (<c>cpuUsageUsec</c>) rather than a percentage, so this needs two samples and the elapsed time
    /// between them — which is why it is not a one-line mapping like the lists above.
    /// </summary>
    public IAsyncEnumerable<ContainerStats> StreamStatsAsync(string id, CancellationToken ct = default) =>
        throw new NotSupportedException(NotYetBuilt);

    /// <summary>Not available — see <see cref="EventsUnsupported"/>.</summary>
    public IAsyncEnumerable<EngineEvent> StreamEventsAsync(CancellationToken ct = default) =>
        throw new NotSupportedException(EventsUnsupported);
}
