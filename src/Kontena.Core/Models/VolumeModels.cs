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

/// <summary>One entry in a volume's contents, as the browser shows it.</summary>
/// <param name="Name">Entry name within the directory being listed.</param>
/// <param name="IsDirectory">Whether it can be opened.</param>
/// <param name="SizeBytes">Size for files; 0 for directories.</param>
/// <param name="ModifiedAt">Last-modified time as the engine reports it, or null when absent.</param>
public sealed record VolumeEntry(string Name, bool IsDirectory, long SizeBytes, DateTimeOffset? ModifiedAt);

/// <summary>
/// One directory's worth of a volume's contents.
/// </summary>
/// <param name="Path">The absolute path inside the volume that was listed.</param>
/// <param name="Entries">Directories first, then files; both alphabetical.</param>
/// <param name="Truncated">
/// True when the engine's answer was cut short. Listing works by streaming an archive of the path, so
/// a directory with tens of thousands of entries is bounded rather than read to the end — a browser
/// that stalls for a minute is worse than one that says it stopped early.
/// </param>
public sealed record VolumeListing(string Path, IReadOnlyList<VolumeEntry> Entries, bool Truncated);
