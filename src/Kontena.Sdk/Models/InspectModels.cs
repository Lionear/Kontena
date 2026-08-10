namespace Kontena.Sdk.Models;

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

    /// <summary>
    /// Whether the engine killed the container for exceeding its memory limit. Exit code 137 alone
    /// cannot answer that — it is 128+SIGKILL, and any external <c>kill -9</c> produces the same
    /// number — so this is what separates an OOM kill from a guess (KON-150).
    /// </summary>
    public bool OomKilled { get; init; }

    /// <summary>How often the engine has restarted this container under its restart policy.</summary>
    public int RestartCount { get; init; }

    /// <summary>
    /// Memory limit in bytes, or null when the container may use what the host has. Unlike the live
    /// stats, this survives the container stopping — which is exactly when it is needed.
    /// </summary>
    public long? MemoryLimitBytes { get; init; }

    /// <summary>
    /// What went wrong starting the container, in the runtime's own words — empty when nothing did.
    /// A container whose command does not exist never runs at all: it stays <c>Created</c>, so there
    /// is no exit code and no log, and this is the only field that says why (KON-150).
    /// </summary>
    public string Error { get; init; } = string.Empty;

    public RestartPolicy RestartPolicy { get; init; }

    /// <summary>Entry point and command joined into a single line.</summary>
    public string Command { get; init; } = string.Empty;

    /// <summary>
    /// The entry point as separate arguments. <see cref="Command"/> joins entry point and command
    /// into one line for display, and that line cannot be split back apart once an argument contains
    /// a space or a quote — so anything that has to *re-run* this container reads these two lists.
    /// </summary>
    public IReadOnlyList<string> Entrypoint { get; init; } = [];

    /// <summary>The command as separate arguments. See <see cref="Entrypoint"/>.</summary>
    public IReadOnlyList<string> Cmd { get; init; } = [];

    public string WorkingDirectory { get; init; } = string.Empty;
    public string User { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } =
        new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Labels { get; init; } =
        new Dictionary<string, string>();

    public IReadOnlyList<InspectMount> Mounts { get; init; } = [];
    public IReadOnlyList<InspectNetwork> Networks { get; init; } = [];
}
