using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kontena.Adapters.Apple;

// The shapes `container --format json` prints, captured against a real install rather than taken from
// its docs — see Depot kontena/Notes/apple-container-cli-formats.md. Names are camelCase in the JSON;
// AppleCli's serializer options are case-insensitive, so these keep C# casing.
//
// Every property is nullable or defaulted on purpose. These records describe another product's output,
// and a field that stops being printed should leave one column empty rather than fail the whole list.

/// <summary>
/// One entry of <c>container list --format json</c>. <c>container inspect</c> prints this same shape —
/// it is the list filtered by id, not a richer second model the way Docker's inspect is — so both go
/// through this one record.
/// </summary>
internal sealed record AppleContainer
{
    public string Id { get; init; } = string.Empty;

    public AppleContainerConfiguration? Configuration { get; init; }

    public AppleContainerStatus? Status { get; init; }
}

internal sealed record AppleContainerConfiguration
{
    public DateTimeOffset? CreationDate { get; init; }

    public AppleImageReference? Image { get; init; }

    public Dictionary<string, string>? Labels { get; init; }

    public List<AppleMount>? Mounts { get; init; }

    /// <summary>
    /// The networks the container is attached to. Read from the configuration rather than from
    /// <c>status</c>: a stopped container has an empty <c>status.networks</c> but still belongs to its
    /// network, and a network's "attached containers" column that empties itself when things stop would
    /// be describing the wrong thing.
    /// </summary>
    public List<AppleContainerNetwork>? Networks { get; init; }

    public List<ApplePublishedPort>? PublishedPorts { get; init; }

    public AppleInitProcess? InitProcess { get; init; }

    public AppleResources? Resources { get; init; }
}

/// <summary>A container's attachment to one network. The name is the network's id.</summary>
internal sealed record AppleContainerNetwork
{
    public string Network { get; init; } = string.Empty;
}

/// <summary>The process the container was created to run — this CLI's equivalent of Docker's Config.</summary>
internal sealed record AppleInitProcess
{
    public string Executable { get; init; } = string.Empty;

    public List<string>? Arguments { get; init; }

    /// <summary>Environment as <c>KEY=value</c> strings, the OCI form — not a map.</summary>
    public List<string>? Environment { get; init; }

    public string WorkingDirectory { get; init; } = string.Empty;

    public AppleProcessUser? User { get; init; }
}

internal sealed record AppleProcessUser
{
    public AppleUserId? Id { get; init; }
}

internal sealed record AppleUserId
{
    public int Uid { get; init; }

    public int Gid { get; init; }
}

/// <summary>
/// What the container's VM was given. Every container here is its own lightweight VM, so these are
/// allocations rather than the cgroup ceilings Docker reports — <c>memoryInBytes</c> is still the
/// figure a memory gauge should measure against.
/// </summary>
internal sealed record AppleResources
{
    public long MemoryInBytes { get; init; }

    public int Cpus { get; init; }
}

internal sealed record AppleContainerStatus
{
    /// <summary>Observed values: <c>running</c>, <c>stopped</c>. Mapped in <see cref="AppleMap"/>.</summary>
    public string State { get; init; } = string.Empty;

    public DateTimeOffset? StartedDate { get; init; }

    /// <summary>
    /// The addresses the container actually got. Empty while it is stopped, which is why the
    /// configuration's network list — not this one — answers "which networks does it belong to".
    /// </summary>
    public List<AppleNetworkAttachment>? Networks { get; init; }
}

/// <summary>One live network attachment, with the addresses assigned to it.</summary>
internal sealed record AppleNetworkAttachment
{
    public string Network { get; init; } = string.Empty;

    /// <summary>Address in CIDR form, e.g. <c>192.168.64.2/24</c>.</summary>
    public string Ipv4Address { get; init; } = string.Empty;

    public string Ipv4Gateway { get; init; } = string.Empty;
}

internal sealed record AppleImageReference
{
    /// <summary>Full reference, e.g. <c>docker.io/library/alpine:3.20</c>.</summary>
    public string Reference { get; init; } = string.Empty;

    public AppleDescriptor? Descriptor { get; init; }
}

/// <summary>An OCI descriptor. Only the digest is read; it is the closest thing to Docker's image id.</summary>
internal sealed record AppleDescriptor
{
    public string Digest { get; init; } = string.Empty;
}

/// <summary>
/// A published port. The protocol field is named <c>proto</c>, not <c>protocol</c> — one of the few
/// places where guessing from the Docker equivalent would have produced a silently empty column.
/// </summary>
internal sealed record ApplePublishedPort
{
    public int ContainerPort { get; init; }

    public int HostPort { get; init; }

    public string Proto { get; init; } = "tcp";
}

/// <summary>
/// A mount. <c>type</c> is a tagged union keyed by kind (<c>{"volume": {"name": "…", …}}</c> for a named
/// volume), so it is kept as raw JSON and read by <see cref="VolumeName"/> rather than modelled: this
/// adapter only needs to know which named volume a container uses, and modelling the other arms would
/// be inventing shapes no capture has seen.
/// </summary>
internal sealed record AppleMount
{
    public string Destination { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public JsonElement Type { get; init; }

    /// <summary>The named volume this mount uses, or null when it is a bind mount or unreadable.</summary>
    public string? VolumeName =>
        Type.ValueKind == JsonValueKind.Object &&
        Type.TryGetProperty("volume", out var volume) &&
        volume.ValueKind == JsonValueKind.Object &&
        volume.TryGetProperty("name", out var name) &&
        name.ValueKind == JsonValueKind.String
            ? name.GetString()
            : null;
}

/// <summary>One entry of <c>container image list --format json</c>.</summary>
internal sealed record AppleImage
{
    /// <summary>Digest without the <c>sha256:</c> prefix that <c>descriptor.digest</c> carries.</summary>
    public string Id { get; init; } = string.Empty;

    public AppleImageConfiguration? Configuration { get; init; }

    public List<AppleImageVariant>? Variants { get; init; }
}

internal sealed record AppleImageConfiguration
{
    public DateTimeOffset? CreationDate { get; init; }

    /// <summary>Full reference; repository and tag have no fields of their own.</summary>
    public string Name { get; init; } = string.Empty;
}

/// <summary>
/// One platform's copy of an image. The size lives here, never on the image itself, and a multi-arch
/// image also carries attestation entries whose platform is literally <c>unknown</c> — see
/// <see cref="IsRealPlatform"/>.
/// </summary>
internal sealed record AppleImageVariant
{
    public long Size { get; init; }

    public ApplePlatform? Platform { get; init; }

    /// <summary>
    /// False for the attestation entries a multi-arch index carries alongside the real images. They are
    /// ~79 KB each and there is one per platform, so counting them would roughly double the reported
    /// size of every multi-arch image.
    /// </summary>
    public bool IsRealPlatform =>
        Platform is not null &&
        !string.Equals(Platform.Architecture, "unknown", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(Platform.Os, "unknown", StringComparison.OrdinalIgnoreCase);
}

internal sealed record ApplePlatform
{
    public string Architecture { get; init; } = string.Empty;

    [JsonPropertyName("os")]
    public string Os { get; init; } = string.Empty;
}

/// <summary>One entry of <c>container volume list --format json</c>.</summary>
internal sealed record AppleVolume
{
    public string Id { get; init; } = string.Empty;

    public AppleVolumeConfiguration? Configuration { get; init; }
}

internal sealed record AppleVolumeConfiguration
{
    public string Name { get; init; } = string.Empty;

    public string Driver { get; init; } = "local";

    /// <summary>Path of the backing disk image on the host.</summary>
    public string Source { get; init; } = string.Empty;
}

/// <summary>One entry of <c>container network list --format json</c>.</summary>
internal sealed record AppleNetwork
{
    public string Id { get; init; } = string.Empty;

    public AppleNetworkConfiguration? Configuration { get; init; }

    public AppleNetworkStatus? Status { get; init; }
}

internal sealed record AppleNetworkConfiguration
{
    public string Name { get; init; } = string.Empty;

    /// <summary>Observed: <c>nat</c>.</summary>
    public string Mode { get; init; } = string.Empty;

    public Dictionary<string, string>? Labels { get; init; }

    /// <summary>
    /// The label Apple puts on the networks it ships. Reading this beats matching on the name
    /// <c>default</c>: a user-created network could be called that, and this cannot.
    /// </summary>
    public const string RoleLabel = "com.apple.container.resource.role";

    public bool IsBuiltIn =>
        Labels is not null &&
        Labels.TryGetValue(RoleLabel, out var role) &&
        string.Equals(role, "builtin", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The subnet lives on the status, not the configuration — it is assigned, not requested.</summary>
internal sealed record AppleNetworkStatus
{
    public string? Ipv4Subnet { get; init; }
}

/// <summary>
/// One entry of <c>container stats --format json --no-stream</c>. Every figure is a plain integer —
/// no <c>"13.11MiB"</c> to parse — but <see cref="CpuUsageUsec"/> is a counter that only rises, so a
/// percentage exists only between two of these.
/// </summary>
internal sealed record AppleStats
{
    public string Id { get; init; } = string.Empty;

    /// <summary>Total CPU time consumed since the container started, in microseconds.</summary>
    public long CpuUsageUsec { get; init; }

    public long MemoryUsageBytes { get; init; }

    public long MemoryLimitBytes { get; init; }

    public long NetworkRxBytes { get; init; }

    public long NetworkTxBytes { get; init; }

    public long BlockReadBytes { get; init; }

    public long BlockWriteBytes { get; init; }
}

/// <summary>
/// What <c>container system df --format json</c> reports — one object, not a list, with a block per
/// category. This is where a prune's byte figure comes from: the CLI announces it as a localised
/// sentence ("Reclaimed 1,37 GB in disk space" on a Dutch machine, and literally "Reclaimed Zero KB"
/// when it removed nothing), while these are plain integers.
/// </summary>
internal sealed record AppleDiskUsage
{
    public AppleDiskUsageEntry? Containers { get; init; }

    public AppleDiskUsageEntry? Images { get; init; }

    public AppleDiskUsageEntry? Volumes { get; init; }
}

internal sealed record AppleDiskUsageEntry
{
    /// <summary>
    /// Total on disk for this category. The one to measure a prune against — <c>reclaimable</c> is not,
    /// because pruning containers makes their image reclaimable and that figure then <em>rises</em>
    /// across a prune that removed nothing from it.
    /// </summary>
    public long SizeInBytes { get; init; }
}

/// <summary>
/// One entry of <c>container system version --format json</c>. Two are printed: the CLI, whose
/// <c>version</c> is a bare number, and the apiserver, whose <c>version</c> is a whole sentence.
/// </summary>
internal sealed record AppleVersion
{
    public string AppName { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    /// <summary>The CLI's own entry — the only one whose <c>version</c> is a version.</summary>
    public const string CliAppName = "container";
}
