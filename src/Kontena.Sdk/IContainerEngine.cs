using Kontena.Sdk;
using Kontena.Sdk.Models;

namespace Kontena.Sdk;

/// <summary>
/// The Container Engine Abstraction Layer (CEAL).
/// <para>
/// One stable, engine-neutral contract that every backend adapter (Docker,
/// Podman, and later Apple <c>container</c>) implements. The UI and business
/// logic only ever talk to this interface — never to a specific engine.
/// </para>
/// <para>
/// Design principle: <b>model the union of capabilities, expose the intersection
/// cleanly, degrade gracefully at the edges.</b> Where engines differ, adapters
/// map their native representation into the neutral models and advertise support
/// through <see cref="Capabilities"/> so the UI can adapt per backend.
/// </para>
/// Every operation is asynchronous and honors its <see cref="CancellationToken"/>.
/// Failures surface as typed <see cref="Kontena.Sdk.Errors.EngineException"/>s.
/// </summary>
public interface IContainerEngine : IBackend
{
    /// <summary>What this engine supports; the UI queries this to adapt.</summary>
    EngineCapabilities Capabilities { get; }

    // ── Containers ──────────────────────────────────────────────────────────

    /// <summary>List containers. <paramref name="all"/> includes stopped ones.</summary>
    ValueTask<IReadOnlyList<ContainerSummary>> ListContainersAsync(
        bool all = true, CancellationToken ct = default);

    /// <summary>Create (and optionally start) a container. Returns its id.</summary>
    /// <remarks>
    /// A <see cref="CreateContainerRequest.RestartPolicy"/> other than <see cref="Models.RestartPolicy.No"/>
    /// requires <see cref="EngineCapabilities.SupportsRestartPolicy"/>. An engine without one must throw
    /// rather than drop the policy: accepting it silently hands back a container the caller believes will
    /// come back after a crash.
    /// </remarks>
    ValueTask<string> CreateContainerAsync(
        CreateContainerRequest request, CancellationToken ct = default);

    ValueTask StartContainerAsync(string id, CancellationToken ct = default);
    ValueTask StopContainerAsync(string id, CancellationToken ct = default);
    ValueTask RestartContainerAsync(string id, CancellationToken ct = default);
    ValueTask PauseContainerAsync(string id, CancellationToken ct = default);
    ValueTask UnpauseContainerAsync(string id, CancellationToken ct = default);

    /// <summary>Remove a container. <paramref name="force"/> kills it if running.</summary>
    ValueTask RemoveContainerAsync(string id, bool force = false, CancellationToken ct = default);

    /// <summary>Full, structured configuration and state of a container (the Inspect tab).</summary>
    ValueTask<ContainerInspect> InspectContainerAsync(string id, CancellationToken ct = default);

    /// <summary>Run a one-shot command inside a running container.</summary>
    /// <remarks>Requires <see cref="EngineCapabilities.SupportsExec"/>.</remarks>
    ValueTask<int> ExecAsync(string id, ExecRequest request, CancellationToken ct = default);

    /// <summary>
    /// Open an interactive, attached exec session: a duplex byte channel (with an
    /// optional PTY) to a process running in the container. Used for the terminal.
    /// </summary>
    /// <remarks>Requires <see cref="EngineCapabilities.SupportsExec"/>.</remarks>
    ValueTask<IExecSession> StartExecSessionAsync(
        string id, ExecRequest request, CancellationToken ct = default);

    /// <summary>Remove all stopped containers.</summary>
    ValueTask<PruneResult> PruneContainersAsync(CancellationToken ct = default);

    // ── Images ──────────────────────────────────────────────────────────────

    ValueTask<IReadOnlyList<ImageSummary>> ListImagesAsync(CancellationToken ct = default);

    /// <summary>Pull an image, streaming progress until complete.</summary>
    /// <param name="credential">
    /// Login for the registry the reference points at, or null to pull anonymously. Resolved by the
    /// caller: matching an image to a stored login needs the keychain and the user's settings, which are
    /// not the adapter's business (KON-114).
    /// </param>
    IAsyncEnumerable<PullProgress> PullImageAsync(
        string reference, RegistryCredential? credential = null, CancellationToken ct = default);

    /// <summary>
    /// Checks a registry login against the registry itself, without storing anything. Throws when the
    /// credential is refused.
    /// <para>
    /// Storing a login that does not work is the same class of lie as a switch that does nothing: it
    /// looks configured and fails later, at a pull, with an error that points at the image instead of the
    /// account (KON-114).
    /// </para>
    /// </summary>
    ValueTask VerifyRegistryLoginAsync(RegistryCredential credential, CancellationToken ct = default);

    /// <summary>Build an image from a Dockerfile, streaming builder output until it completes.</summary>
    /// <remarks>Requires <see cref="EngineCapabilities.SupportsBuild"/>.</remarks>
    IAsyncEnumerable<BuildProgress> BuildImageAsync(
        BuildRequest request, CancellationToken ct = default);

    ValueTask RemoveImageAsync(string id, bool force = false, CancellationToken ct = default);

    /// <summary>
    /// Read an image's baked-in config (exposed ports, declared volumes, env) to
    /// pre-fill the Run flow. Returns null when the image is not present locally.
    /// </summary>
    ValueTask<ImageConfig?> InspectImageAsync(string reference, CancellationToken ct = default);

    /// <summary>
    /// Gives the image an additional name. <paramref name="newTag"/> is a full reference
    /// (<c>ghcr.io/me/app:1.2</c>, or <c>app</c> for <c>app:latest</c>) — the registry it names is where a
    /// following <see cref="PushImageAsync"/> would send it.
    /// </summary>
    /// <remarks>The old name stays; nothing is renamed and nothing is removed.</remarks>
    ValueTask TagImageAsync(string id, string newTag, CancellationToken ct = default);

    /// <summary>Push an image to the registry its reference names, streaming progress until complete.</summary>
    /// <param name="reference">
    /// The name to push, registry included — <c>ghcr.io/me/app:1.2</c>. An image only reaches a registry
    /// under a name that points at it, so the caller tags first (<see cref="TagImageAsync"/>) when the
    /// local name is not the one to publish.
    /// </param>
    /// <param name="credential">
    /// Login for the registry the reference points at, or null to push anonymously. Resolved by the
    /// caller, for the same reason a pull's is (KON-114) — and unlike a pull, a registry that takes an
    /// anonymous push is the exception rather than the rule.
    /// </param>
    IAsyncEnumerable<PushProgress> PushImageAsync(
        string reference, RegistryCredential? credential = null, CancellationToken ct = default);

    /// <summary>Remove unused images. <paramref name="allUnused"/> also removes tagged
    /// images not used by any container (not just dangling ones).</summary>
    /// <remarks>Requires <see cref="EngineCapabilities.SupportsPrune"/>.</remarks>
    ValueTask<PruneResult> PruneImagesAsync(bool allUnused = true, CancellationToken ct = default);

    // ── Volumes ─────────────────────────────────────────────────────────────

    ValueTask<IReadOnlyList<VolumeSummary>> ListVolumesAsync(CancellationToken ct = default);
    ValueTask<VolumeSummary> CreateVolumeAsync(
        CreateVolumeRequest request, CancellationToken ct = default);
    ValueTask RemoveVolumeAsync(string name, bool force = false, CancellationToken ct = default);

    /// <summary>
    /// Lists what is inside a volume at <paramref name="path"/> (<c>/</c> for its root).
    /// <para>
    /// No engine offers this directly — a volume is only readable through a container — so an adapter
    /// mounts it into a throwaway container to answer. That means it can fail for reasons that have
    /// nothing to do with the volume, and it is gated behind
    /// <see cref="EngineCapabilities.SupportsVolumeBrowse"/>.
    /// </para>
    /// </summary>
    /// <remarks>Requires <see cref="EngineCapabilities.SupportsVolumeBrowse"/>.</remarks>
    ValueTask<VolumeListing> BrowseVolumeAsync(
        string name, string path = "/", CancellationToken ct = default);

    /// <summary>
    /// Writes a volume's contents to a tar archive at <paramref name="archivePath"/> on the host.
    /// <para>
    /// A tar and not an unpacked directory, because ownership has to survive the trip: a host
    /// filesystem writes those files as the logged-in user, so a volume owned by uid 999 would arrive
    /// as somebody else and the container that needs it would not start. A tar carries uid, gid and
    /// mode, and is unpacked back inside a container, where root can restore them.
    /// </para>
    /// <para>Entries are relative to the volume root — <c>./data/file</c>, never <c>/mnt/data/file</c>.</para>
    /// </summary>
    /// <remarks>Requires <see cref="EngineCapabilities.SupportsVolumeTransfer"/>.</remarks>
    ValueTask ExportVolumeAsync(string name, string archivePath, CancellationToken ct = default);

    /// <summary>
    /// Unpacks a tar archive written by <see cref="ExportVolumeAsync"/> into a volume, which must
    /// already exist. Existing files with the same paths are overwritten; nothing else is removed.
    /// </summary>
    /// <remarks>Requires <see cref="EngineCapabilities.SupportsVolumeTransfer"/>.</remarks>
    ValueTask ImportVolumeAsync(string name, string archivePath, CancellationToken ct = default);

    /// <summary>Remove all volumes not used by any container.</summary>
    ValueTask<PruneResult> PruneVolumesAsync(CancellationToken ct = default);

    // ── Networks ────────────────────────────────────────────────────────────

    ValueTask<IReadOnlyList<NetworkSummary>> ListNetworksAsync(CancellationToken ct = default);
    ValueTask<NetworkSummary> CreateNetworkAsync(
        CreateNetworkRequest request, CancellationToken ct = default);
    ValueTask RemoveNetworkAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Attaches a container to a network. Works on a stopped container too — the effect simply shows when
    /// it next starts, which the UI must not read as a failure (KON-115).
    /// </summary>
    ValueTask ConnectNetworkAsync(string containerId, string networkId, CancellationToken ct = default);

    /// <summary>
    /// Detaches a container from a network. <paramref name="force"/> asks the engine to do it even when
    /// the container is running.
    /// </summary>
    ValueTask DisconnectNetworkAsync(
        string containerId, string networkId, bool force = false, CancellationToken ct = default);

    // ── Compose ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Bring a Compose project up from a compose file, streaming the toolchain's
    /// per-service create / pull / build output until it completes. "Down", combined
    /// start/stop, and aggregated logs are composed from the container primitives above
    /// (by <c>com.docker.compose.project</c> label), so only "up" needs engine-native Compose.
    /// </summary>
    /// <remarks>Requires <see cref="EngineCapabilities.SupportsCompose"/>.</remarks>
    IAsyncEnumerable<ComposeProgress> ComposeUpAsync(
        ComposeUpRequest request, CancellationToken ct = default);

    // ── Streams ─────────────────────────────────────────────────────────────

    /// <summary>Stream a container's logs. <paramref name="follow"/> keeps it open.</summary>
    IAsyncEnumerable<LogEntry> StreamLogsAsync(
        string id, bool follow = true, CancellationToken ct = default);

    /// <summary>Stream live resource-usage samples for a container.</summary>
    /// <remarks>Requires <see cref="EngineCapabilities.SupportsStats"/>.</remarks>
    IAsyncEnumerable<ContainerStats> StreamStatsAsync(string id, CancellationToken ct = default);

    /// <summary>Stream engine-wide events (container/image/volume/network changes).</summary>
    /// <remarks>Requires <see cref="EngineCapabilities.SupportsEvents"/>.</remarks>
    IAsyncEnumerable<EngineEvent> StreamEventsAsync(CancellationToken ct = default);
}
