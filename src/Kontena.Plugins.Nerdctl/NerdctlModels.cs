namespace Kontena.Plugins.Nerdctl;

// The `--format json` row shapes: one type per listing command, each mirroring a captured fixture
// field for field (Notes/nerdctl-cli-formats.md, Notes/nerdctl-advanced-formats.md). They belong
// together because they share one trait that decides how every one of them is read — nerdctl formats
// these for a terminal column, so a field that looks numeric is a string, and a size or timestamp
// needs NerdctlJson before it means anything.
//
// `nerdctl inspect`'s payload is a different animal — Docker's own shape, nested, and typed — so it
// lives in NerdctlInspectModels.cs instead.

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
/// One row of <c>nerdctl volume ls --format json</c>, matching the populated row captured in
/// <c>Fixtures/volume-ls.json</c> (<c>{"Driver":"local","Labels":"","Mountpoint":"...","Name":"...",
/// "Scope":"local","Size":""}</c>). <see cref="Size"/> is left as the raw string: it was empty even for
/// a real volume in that capture, so there is no observed non-empty case to parse against, and
/// inventing a byte count from nothing would be a wrong answer stated as fact.
/// </summary>
public sealed class NerdctlVolume
{
    public string Name { get; init; } = string.Empty;
    public string Driver { get; init; } = string.Empty;
    public string Mountpoint { get; init; } = string.Empty;

    /// <summary>Comma-joined, same shape as <see cref="NerdctlContainer.Labels"/>. Observed empty in the capture.</summary>
    public string Labels { get; init; } = string.Empty;

    /// <summary>Observed as the literal empty string even for a real volume — see <see cref="NerdctlMap.ToVolume"/>.</summary>
    public string Size { get; init; } = string.Empty;
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

/// <summary>
/// One row of <c>nerdctl stats --no-stream --format json</c>. Every value is formatted for a terminal
/// column, not for a caller (Notes/nerdctl-advanced-formats.md): the percentages carry their <c>%</c>,
/// and three fields pack two values into one string separated by <c>" / "</c>. The sizes here are
/// <b>binary</b> ("13.11MiB") unlike <see cref="NerdctlImage.Size"/>'s decimal ones — read them with
/// <see cref="NerdctlJson.BinarySize"/>, never <see cref="NerdctlJson.Size"/>.
/// </summary>
public sealed class NerdctlStats
{
    public string Name { get; init; } = string.Empty;

    /// <summary>The <b>short</b> id, not the 64-character one — see <see cref="NerdctlMap.ToStats"/> for why it is not used as the sample's id.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>e.g. <c>"0.00%"</c> — read with <see cref="NerdctlJson.Percent"/>.</summary>
    public string CpuPerc { get; init; } = string.Empty;

    /// <summary>Used and limit in one string: <c>"13.11MiB / 62.7GiB"</c> — split with <see cref="NerdctlJson.Pair"/>.</summary>
    public string MemUsage { get; init; } = string.Empty;

    /// <summary>Already derivable from <see cref="MemUsage"/>; <see cref="Kontena.Sdk.Models.ContainerStats"/> computes its own fraction, so this is kept only so the row round-trips.</summary>
    public string MemPerc { get; init; } = string.Empty;

    /// <summary>Received and transmitted in one string: <c>"0B / 0B"</c>.</summary>
    public string NetIo { get; init; } = string.Empty;

    /// <summary>Read and written in one string: <c>"0B / 0B"</c>.</summary>
    public string BlockIo { get; init; } = string.Empty;

    /// <summary>A number as a string. <see cref="Kontena.Sdk.Models.ContainerStats"/> has no field for it, so it is not mapped.</summary>
    public string Pids { get; init; } = string.Empty;
}

/// <summary>
/// One record of <c>nerdctl events --format json</c> — NDJSON with a blank line between records. Three
/// of these fields lie about what they look like (Notes/nerdctl-advanced-formats.md), which is why this
/// DTO carries doc comments where the other row types do not.
/// </summary>
public sealed class NerdctlEvent
{
    public string Timestamp { get; init; } = string.Empty;

    /// <summary>
    /// <b>Empty on every observed event.</b> The real id is nested inside <see cref="Event"/>; reading
    /// this field gives an empty string with no error anywhere — see <see cref="NerdctlJson.NestedId"/>.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    public string Namespace { get; init; } = string.Empty;

    /// <summary>
    /// containerd's own topic (<c>/containers/create</c>, <c>/tasks/start</c>, <c>/snapshot/prepare</c>),
    /// <b>not</b> Docker's action name — matching on "start"/"die"/"stop" finds nothing. This is the only
    /// field that says what happened; <see cref="NerdctlMap.ToEvent"/> maps it.
    /// </summary>
    public string Topic { get; init; } = string.Empty;

    /// <summary>Literally <c>"unknown"</c> on every observed event — carried for round-tripping, never read.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>The topic's payload as an <b>escaped JSON string</b>, not an object — parse with <see cref="NerdctlJson.NestedId"/>.</summary>
    public string Event { get; init; } = string.Empty;
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
