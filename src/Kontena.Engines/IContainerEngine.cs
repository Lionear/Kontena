using Kontena.Core;
using Kontena.Core.Models;

namespace Kontena.Engines;

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
/// Failures surface as typed <see cref="Kontena.Core.Errors.EngineException"/>s.
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
    ValueTask TagImageAsync(string id, string newTag, CancellationToken ct = default);

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

    /// <summary>Remove all volumes not used by any container.</summary>
    ValueTask<PruneResult> PruneVolumesAsync(CancellationToken ct = default);

    // ── Networks ────────────────────────────────────────────────────────────

    ValueTask<IReadOnlyList<NetworkSummary>> ListNetworksAsync(CancellationToken ct = default);
    ValueTask<NetworkSummary> CreateNetworkAsync(
        CreateNetworkRequest request, CancellationToken ct = default);
    ValueTask RemoveNetworkAsync(string id, CancellationToken ct = default);

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
