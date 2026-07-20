namespace Kontena.Core.Models;

/// <summary>A published port mapping (host → container).</summary>
/// <param name="HostPort">Port on the host, or null when not published.</param>
/// <param name="ContainerPort">Port inside the container.</param>
/// <param name="Protocol">Transport protocol, e.g. "tcp" or "udp".</param>
public sealed record PortBinding(int? HostPort, int ContainerPort, string Protocol = "tcp");

/// <summary>Engine-neutral summary of a container as shown in lists.</summary>
public sealed record ContainerSummary
{
    /// <summary>Full engine-assigned id.</summary>
    public required string Id { get; init; }

    /// <summary>Primary display name.</summary>
    public required string Name { get; init; }

    /// <summary>Image reference the container was created from.</summary>
    public required string Image { get; init; }

    /// <summary>Normalized lifecycle state.</summary>
    public required ContainerState State { get; init; }

    /// <summary>Raw human-readable status text from the engine (e.g. "Up 2 hours").</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Published ports.</summary>
    public IReadOnlyList<PortBinding> Ports { get; init; } = [];

    /// <summary>When the container was created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Backend id that owns this container (e.g. "docker", "podman").</summary>
    public required string Backend { get; init; }
}

/// <summary>Request describing a container to create and (optionally) run.</summary>
public sealed record CreateContainerRequest
{
    /// <summary>Image reference to run. Pulled automatically if missing.</summary>
    public required string Image { get; init; }

    /// <summary>Optional container name; engine auto-generates one when null.</summary>
    public string? Name { get; init; }

    /// <summary>Port mappings to publish.</summary>
    public IReadOnlyList<PortBinding> Ports { get; init; } = [];

    /// <summary>Environment variables (KEY → value).</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Volume mounts (source volume/path → container path).</summary>
    public IReadOnlyDictionary<string, string> Volumes { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Network to attach to; engine default when null.</summary>
    public string? Network { get; init; }

    /// <summary>Restart policy.</summary>
    public RestartPolicy RestartPolicy { get; init; } = RestartPolicy.No;

    /// <summary>Start the container immediately after creation.</summary>
    public bool Start { get; init; } = true;
}

/// <summary>Command to execute inside a running container.</summary>
public sealed record ExecRequest
{
    /// <summary>Command and arguments, e.g. ["/bin/sh", "-c", "ls"].</summary>
    public required IReadOnlyList<string> Command { get; init; }

    /// <summary>Allocate a pseudo-TTY.</summary>
    public bool Tty { get; init; }

    /// <summary>Working directory inside the container.</summary>
    public string? WorkingDirectory { get; init; }
}
