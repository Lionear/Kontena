namespace Kontena.Core.Models;

/// <summary>
/// Identity and health of a connected backend — engine or cluster. This is the shared
/// shape the switcher chrome and title bar read for any backend; richer per-axis detail
/// (e.g. a cluster's distribution and node count) lives on derived records like
/// <c>ClusterInfo</c>. Returned by <see cref="IBackend.GetInfoAsync"/>.
/// </summary>
public record BackendInfo
{
    /// <summary>Backend id, e.g. "docker", "podman", "kubernetes".</summary>
    public required string Backend { get; init; }

    /// <summary>Display name, e.g. "Docker" or a kube-context name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Human label for the kind of backend, e.g. "container engine" or "Kubernetes".</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Backend version string (engine version, or cluster server version).</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Endpoint Kontena talks to (socket path, pipe, or API server URL).</summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>Current connection state.</summary>
    public EngineConnectionState ConnectionState { get; init; } = EngineConnectionState.Unknown;
}

/// <summary>
/// What a given engine supports. The UI queries these flags to adapt — hiding,
/// disabling, or offering alternate paths for features the active backend lacks.
/// This is how one UI serves engines that are not identical.
/// </summary>
public sealed record EngineCapabilities
{
    /// <summary>Runs containers rootless.</summary>
    public bool Rootless { get; init; }

    /// <summary>Can build images.</summary>
    public bool SupportsBuild { get; init; }

    /// <summary>Can manage Compose-style multi-container projects.</summary>
    public bool SupportsCompose { get; init; }

    /// <summary>Can exec into running containers.</summary>
    public bool SupportsExec { get; init; }

    /// <summary>Can prune unused resources.</summary>
    public bool SupportsPrune { get; init; }

    /// <summary>
    /// Can list the contents of a volume. Needs a locally available image to mount it into, so this is
    /// answered per engine rather than assumed — see <c>BrowseVolumeAsync</c>.
    /// </summary>
    public bool SupportsVolumeBrowse { get; init; }

    /// <summary>Supports GPU passthrough.</summary>
    public bool SupportsGpu { get; init; }

    /// <summary>Streams live resource stats.</summary>
    public bool SupportsStats { get; init; }

    /// <summary>Streams engine events.</summary>
    public bool SupportsEvents { get; init; }
}
