namespace Kontena.Core.Models;

/// <summary>A mount attached to a container (bind, volume, or tmpfs).</summary>
/// <param name="Type">Mount kind, e.g. "bind" or "volume".</param>
/// <param name="Source">Host path or volume name.</param>
/// <param name="Destination">Path inside the container.</param>
/// <param name="ReadWrite">True when writable, false when mounted read-only.</param>
public sealed record InspectMount(string Type, string Source, string Destination, bool ReadWrite);

/// <summary>A network the container is attached to, with its addressing.</summary>
/// <param name="Name">Network name.</param>
/// <param name="IpAddress">Container's IP on this network.</param>
/// <param name="Gateway">Gateway for this network.</param>
public sealed record InspectNetwork(string Name, string IpAddress, string Gateway);

/// <summary>
/// Engine-neutral, structured view of a container's full configuration and state
/// — what the Inspect tab renders. Adapters map their native inspect payload into
/// this shape.
/// </summary>
public sealed record ContainerInspect
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Image { get; init; }
    public string ImageId { get; init; } = string.Empty;

    public ContainerState State { get; init; }

    /// <summary>Raw status string from the engine (e.g. "running", "exited").</summary>
    public string Status { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }

    public int ExitCode { get; init; }
    public int Pid { get; init; }

    public RestartPolicy RestartPolicy { get; init; }

    /// <summary>Entry point and command joined into a single line.</summary>
    public string Command { get; init; } = string.Empty;
    public string WorkingDirectory { get; init; } = string.Empty;
    public string User { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } =
        new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Labels { get; init; } =
        new Dictionary<string, string>();

    public IReadOnlyList<InspectMount> Mounts { get; init; } = [];
    public IReadOnlyList<InspectNetwork> Networks { get; init; } = [];
}
