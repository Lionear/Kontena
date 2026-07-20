namespace Kontena.Core.Models;

/// <summary>Identity and health of a connected engine.</summary>
public sealed record EngineInfo
{
    /// <summary>Backend id, e.g. "docker" or "podman".</summary>
    public required string Backend { get; init; }

    /// <summary>Display name, e.g. "Docker".</summary>
    public required string DisplayName { get; init; }

    /// <summary>Engine version string.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Endpoint Kontena talks to (socket path or pipe).</summary>
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

    /// <summary>Supports GPU passthrough.</summary>
    public bool SupportsGpu { get; init; }

    /// <summary>Streams live resource stats.</summary>
    public bool SupportsStats { get; init; }

    /// <summary>Streams engine events.</summary>
    public bool SupportsEvents { get; init; }
}
