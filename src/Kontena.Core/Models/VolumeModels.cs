namespace Kontena.Core.Models;

/// <summary>Engine-neutral summary of a volume.</summary>
public sealed record VolumeSummary
{
    /// <summary>Volume name (its identity for most engines).</summary>
    public required string Name { get; init; }

    /// <summary>Driver, e.g. "local".</summary>
    public string Driver { get; init; } = "local";

    /// <summary>Host mountpoint path.</summary>
    public string Mountpoint { get; init; } = string.Empty;

    /// <summary>Size in bytes when the engine reports it; otherwise null.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>Names of containers currently using this volume.</summary>
    public IReadOnlyList<string> UsedBy { get; init; } = [];

    /// <summary>True when no container references the volume.</summary>
    public bool IsDangling => UsedBy.Count == 0;
}

/// <summary>Request to create a volume.</summary>
public sealed record CreateVolumeRequest
{
    /// <summary>Desired volume name.</summary>
    public required string Name { get; init; }

    /// <summary>Driver to use.</summary>
    public string Driver { get; init; } = "local";

    /// <summary>Optional driver labels.</summary>
    public IReadOnlyDictionary<string, string> Labels { get; init; } =
        new Dictionary<string, string>();
}
