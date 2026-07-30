namespace Kontena.Sdk.Models;

/// <summary>Engine-neutral summary of an image.</summary>
public sealed record ImageSummary
{
    /// <summary>Full image id (digest or engine id).</summary>
    public required string Id { get; init; }

    /// <summary>Repository, e.g. "docker.io/library/nginx".</summary>
    public required string Repository { get; init; }

    /// <summary>Tag, e.g. "1.27-alpine". "&lt;none&gt;" when untagged.</summary>
    public string Tag { get; init; } = "<none>";

    /// <summary>On-disk size in bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>When the image was created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>True when at least one container uses this image.</summary>
    public bool InUse { get; init; }
}

/// <summary>
/// The bits of an image's baked-in configuration the Run flow uses to scaffold
/// sensible defaults: the ports it exposes and the volume mount points it declares.
/// </summary>
public sealed record ImageConfig
{
    /// <summary>Ports the image declares as exposed (container-side only).</summary>
    public IReadOnlyList<PortBinding> ExposedPorts { get; init; } = [];

    /// <summary>Mount points the image declares as volumes (container paths).</summary>
    public IReadOnlyList<string> Volumes { get; init; } = [];

    /// <summary>Environment defaults baked into the image (KEY → value).</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>();
}

/// <summary>Outcome of a prune operation.</summary>
/// <param name="ItemsDeleted">How many items were removed.</param>
/// <param name="SpaceReclaimedBytes">Disk space freed, in bytes.</param>
public sealed record PruneResult(int ItemsDeleted, long SpaceReclaimedBytes);

/// <summary>Progress update while pulling an image.</summary>
/// <param name="Reference">Image reference being pulled.</param>
/// <param name="Status">Human-readable status line.</param>
/// <param name="Current">Bytes transferred so far, when known.</param>
/// <param name="Total">Total bytes, when known.</param>
public sealed record PullProgress(string Reference, string Status, long? Current, long? Total);
