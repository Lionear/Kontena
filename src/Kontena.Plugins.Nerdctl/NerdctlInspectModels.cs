namespace Kontena.Plugins.Nerdctl;

// ── `nerdctl inspect` — Docker-compatible payload ──────────────────────────────────────────────
//
// `nerdctl inspect <id>` prints a JSON array with one object, not NDJSON like every other command
// here (see Notes/nerdctl-cli-formats.md). Its shape is Docker's own container-inspect shape, so
// these DTOs mirror only the fields Kontena.Adapters.Docker.DockerEngine.MapInspect reads off
// Docker.DotNet's equivalent types — this plugin cannot reference Docker.DotNet itself (it may only
// reference Kontena.Sdk), so the same fields are re-declared here against nerdctl's own JSON.

/// <summary>Restart policy as nerdctl's inspect reports it: a bare name, not Docker.DotNet's enum.</summary>
public sealed class NerdctlInspectRestartPolicy
{
    public string Name { get; init; } = string.Empty;
}

/// <summary>The subset of <c>HostConfig</c> <see cref="NerdctlMap.ToInspect"/> needs.</summary>
public sealed class NerdctlInspectHostConfig
{
    /// <summary>Bytes; 0 means "no limit", same convention as Docker.</summary>
    public long Memory { get; init; }

    public NerdctlInspectRestartPolicy RestartPolicy { get; init; } = new();
}

/// <summary>A mount entry — same fields Docker's mapping reads, nerdctl adds more (Mode, Propagation) that are not needed.</summary>
public sealed class NerdctlInspectMount
{
    public string Type { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Destination { get; init; } = string.Empty;
    public bool RW { get; init; }
}

/// <summary>
/// The subset of <c>Config</c> <see cref="NerdctlMap.ToInspect"/> needs. <see cref="Cmd"/> and
/// <see cref="Entrypoint"/> exist for Docker-compatibility but were absent entirely on every CRI
/// container observed — <see cref="NerdctlMap.ToInspect"/> builds the real command from the
/// container's top-level <c>Path</c>/<c>Args</c> instead, which were always present.
/// </summary>
public sealed class NerdctlInspectConfig
{
    public IReadOnlyList<string>? Env { get; init; }
    public string Image { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string>? Labels { get; init; }
    public string? WorkingDir { get; init; }
    public string? User { get; init; }
    public IReadOnlyList<string>? Cmd { get; init; }
    public IReadOnlyList<string>? Entrypoint { get; init; }
}

/// <summary>One entry of <c>NetworkSettings.Networks</c>. No <c>Gateway</c> key was present on any observed CRI container.</summary>
public sealed class NerdctlInspectNetworkEndpoint
{
    public string? IPAddress { get; init; }
    public string? Gateway { get; init; }
}

/// <summary>The subset of <c>NetworkSettings</c> <see cref="NerdctlMap.ToInspect"/> needs.</summary>
public sealed class NerdctlInspectNetworkSettings
{
    public IReadOnlyDictionary<string, NerdctlInspectNetworkEndpoint> Networks { get; init; } =
        new Dictionary<string, NerdctlInspectNetworkEndpoint>();
}

/// <summary>The subset of <c>State</c> <see cref="NerdctlMap.ToInspect"/> needs. No <c>OOMKilled</c> key — unlike Docker, nerdctl's inspect does not report it.</summary>
public sealed class NerdctlInspectState
{
    public string Status { get; init; } = string.Empty;
    public bool Running { get; init; }
    public bool Paused { get; init; }
    public bool Restarting { get; init; }
    public int Pid { get; init; }
    public int ExitCode { get; init; }
    public string Error { get; init; } = string.Empty;
    public string StartedAt { get; init; } = string.Empty;
    public string FinishedAt { get; init; } = string.Empty;
}

/// <summary>
/// One element of the array <c>nerdctl inspect &lt;id&gt;</c> prints — Docker's own container-inspect
/// shape (see the remarks on this file's inspect section for why these DTOs exist rather than reusing
/// Docker.DotNet's).
/// </summary>
public sealed class NerdctlInspectContainer
{
    public string Id { get; init; } = string.Empty;
    public string Created { get; init; } = string.Empty;

    /// <summary>The binary that was run — combined with <see cref="Args"/> for the real command line; see <see cref="NerdctlInspectConfig"/>.</summary>
    public string Path { get; init; } = string.Empty;

    public IReadOnlyList<string> Args { get; init; } = [];
    public NerdctlInspectState State { get; init; } = new();

    /// <summary>The image reference actually resolved/used.</summary>
    public string Image { get; init; } = string.Empty;

    /// <summary>Observed empty ("") for every CRI-managed container in the captured fixture — see <see cref="NerdctlMap.ToInspect"/> for the fallback this forces.</summary>
    public string Name { get; init; } = string.Empty;

    public int RestartCount { get; init; }
    public NerdctlInspectHostConfig HostConfig { get; init; } = new();
    public IReadOnlyList<NerdctlInspectMount> Mounts { get; init; } = [];
    public NerdctlInspectConfig Config { get; init; } = new();
    public NerdctlInspectNetworkSettings NetworkSettings { get; init; } = new();
}
