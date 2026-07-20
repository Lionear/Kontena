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
public interface IContainerEngine
{
    /// <summary>Backend id, e.g. "docker" or "podman".</summary>
    string Backend { get; }

    /// <summary>What this engine supports; the UI queries this to adapt.</summary>
    EngineCapabilities Capabilities { get; }

    /// <summary>Identity and current health of the engine.</summary>
    ValueTask<EngineInfo> GetInfoAsync(CancellationToken ct = default);

    /// <summary>Lightweight connectivity check. Throws when unreachable.</summary>
    ValueTask PingAsync(CancellationToken ct = default);

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
    IAsyncEnumerable<PullProgress> PullImageAsync(
        string reference, CancellationToken ct = default);

    ValueTask RemoveImageAsync(string id, bool force = false, CancellationToken ct = default);
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

    /// <summary>Remove all volumes not used by any container.</summary>
    ValueTask<PruneResult> PruneVolumesAsync(CancellationToken ct = default);

    // ── Networks ────────────────────────────────────────────────────────────

    ValueTask<IReadOnlyList<NetworkSummary>> ListNetworksAsync(CancellationToken ct = default);
    ValueTask<NetworkSummary> CreateNetworkAsync(
        CreateNetworkRequest request, CancellationToken ct = default);
    ValueTask RemoveNetworkAsync(string id, CancellationToken ct = default);

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
