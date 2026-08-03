namespace Kontena.Plugins.Nerdctl;

/// <summary>
/// One row of <c>nerdctl ps -a --format json</c>, deserialized field-for-field — see
/// Notes/nerdctl-cli-formats.md. Every field is a string because nerdctl prints every field as a
/// string, including the ones a reader expects to be numeric.
/// </summary>
public sealed class NerdctlContainer
{
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Quoted and truncated with an ellipsis (e.g. <c>"\"local-path-provisio…\""</c>) — nerdctl builds
    /// this for a terminal column, not for a caller. It is kept here only so the raw row round-trips;
    /// <see cref="NerdctlMap.ToSummary"/> never reads it, because there is no way to turn a truncated,
    /// quoted fragment back into a real command line. The real command line comes from
    /// <c>nerdctl inspect</c> (<see cref="NerdctlInspectContainer.Path"/> + <see cref="NerdctlInspectContainer.Args"/>).
    /// </summary>
    public string Command { get; init; } = string.Empty;

    public string CreatedAt { get; init; } = string.Empty;
    public string Image { get; init; } = string.Empty;
    public string Platform { get; init; } = string.Empty;

    /// <summary>
    /// The container's identity column. For a plain <c>nerdctl run --name foo</c> container this is
    /// just <c>foo</c>. For a container created through the CRI plugin (i.e. by kubelet) it is instead
    /// <c>k8s://namespace/pod/container</c> — see <see cref="NerdctlMap.ToSummary"/> for what that
    /// becomes.
    /// </summary>
    public string Names { get; init; } = string.Empty;

    public string Ports { get; init; } = string.Empty;

    /// <summary>Bare word ("Up", "Created") — unlike Docker's ps this never carries a duration.</summary>
    public string Status { get; init; } = string.Empty;

    public string Runtime { get; init; } = string.Empty;
    public string Size { get; init; } = string.Empty;

    /// <summary>Comma-joined <c>key=value</c> pairs, not a JSON object — parse with <see cref="NerdctlJson.Labels"/>.</summary>
    public string Labels { get; init; } = string.Empty;
}

/// <summary>
/// One row of <c>nerdctl images --format json</c>. <see cref="Size"/> and <see cref="BlobSize"/> are
/// human strings ("53.98MB"), not byte counts — read them with <see cref="NerdctlJson.Size"/>.
/// </summary>
public sealed class NerdctlImage
{
    /// <summary>Go's default time layout, not ISO8601 — <c>ps</c> and <c>images</c> disagree; read with <see cref="NerdctlJson.Time"/>.</summary>
    public string CreatedAt { get; init; } = string.Empty;

    public string CreatedSince { get; init; } = string.Empty;
    public string Digest { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;

    /// <summary>Repository, or the literal string <c>"&lt;none&gt;"</c> for a dangling image.</summary>
    public string Repository { get; init; } = string.Empty;

    /// <summary>
    /// Tag, or the literal string <c>"&lt;none&gt;"</c> — not empty, not null. Left as-is rather than
    /// normalized: <see cref="Kontena.Sdk.Models.ImageSummary.Tag"/> already defaults to that same
    /// string, so nerdctl's "no tag" and the SDK's "no tag" already agree.
    /// </summary>
    public string Tag { get; init; } = "<none>";

    public string Name { get; init; } = string.Empty;

    /// <summary>On-disk size, uncompressed — the number the CEAL wants. <see cref="BlobSize"/> is the compressed layer size and is not mapped.</summary>
    public string Size { get; init; } = string.Empty;

    public string BlobSize { get; init; } = string.Empty;
    public string Platform { get; init; } = string.Empty;
}

/// <summary>
/// One row of <c>nerdctl network ls --format json</c>. <c>kindnet</c>, <c>host</c> and <c>none</c> all
/// observed with an empty <see cref="Id"/> — see <see cref="NerdctlMap.ToNetwork"/>.
/// </summary>
public sealed class NerdctlNetwork
{
    /// <summary>Empty for several built-in/CNI networks. Never use this as a lookup key — use <see cref="Name"/>.</summary>
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    /// <summary>Comma-joined, same shape as <see cref="NerdctlContainer.Labels"/>. Always observed empty; <see cref="Kontena.Sdk.Models.NetworkSummary"/> has no label field to put it in.</summary>
    public string Labels { get; init; } = string.Empty;
}

/// <summary>
/// One row of <c>nerdctl volume ls --format json</c>. Only <see cref="Name"/> is declared: every other
/// command's populated shape here was captured against a real nerdctl (Notes/nerdctl-cli-formats.md),
/// but <c>volume ls</c> was only ever observed with zero volumes — its empty case is the one edge that
/// capture caught. Guessing a driver/mountpoint field name from nerdctl's docs rather than an actual
/// capture is exactly the risk this file exists to avoid, so those are left unmapped until a populated
/// capture exists (KON-141 PR 3/4).
/// </summary>
public sealed class NerdctlVolume
{
    public string Name { get; init; } = string.Empty;
}

/// <summary>
/// One row of <c>nerdctl namespace ls --format json</c> — a containerd namespace (e.g. <c>k8s.io</c>),
/// not a Kubernetes one. Unlike <c>ps</c>/<c>images</c>/<c>network ls</c>, the counts here are real JSON
/// numbers, not strings.
/// </summary>
public sealed class NerdctlNamespace
{
    public string Name { get; init; } = string.Empty;
    public int Containers { get; init; }
    public int Images { get; init; }
    public int Volumes { get; init; }

    /// <summary>Always observed <c>null</c>; nerdctl's own schema for a populated value is unknown.</summary>
    public string? Labels { get; init; }
}

/// <summary>Log and storage drivers <c>nerdctl info</c> reports as supported.</summary>
public sealed class NerdctlInfoPlugins
{
    public IReadOnlyList<string> Log { get; init; } = [];
    public IReadOnlyList<string> Storage { get; init; } = [];
}

/// <summary>
/// <c>nerdctl info --format json</c> — Docker-shaped, but <see cref="ServerVersion"/> is containerd's
/// version, not nerdctl's, and there is no <c>Rootless</c> field: that reads the same way Docker's does,
/// as <c>"name=rootless"</c> inside <see cref="SecurityOptions"/> (root here shows
/// <c>["name=seccomp,profile=builtin", "name=cgroupns"]</c>, no such entry).
/// </summary>
public sealed class NerdctlInfo
{
    public string Id { get; init; } = string.Empty;
    public string Driver { get; init; } = string.Empty;
    public NerdctlInfoPlugins Plugins { get; init; } = new();
    public string SystemTime { get; init; } = string.Empty;
    public string LoggingDriver { get; init; } = string.Empty;
    public string CgroupDriver { get; init; } = string.Empty;
    public string CgroupVersion { get; init; } = string.Empty;
    public string KernelVersion { get; init; } = string.Empty;
    public string OperatingSystem { get; init; } = string.Empty;
    public string OsType { get; init; } = string.Empty;
    public string Architecture { get; init; } = string.Empty;
    public int NCpu { get; init; }
    public long MemTotal { get; init; }
    public string Name { get; init; } = string.Empty;

    /// <summary>The containerd version, e.g. "v2.3.1" — nerdctl's own version is not in this payload at all.</summary>
    public string ServerVersion { get; init; } = string.Empty;

    public IReadOnlyList<string> SecurityOptions { get; init; } = [];
    public IReadOnlyList<string>? Warnings { get; init; }
}

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
