namespace Kontena.Sdk.Models;

/// <summary>A single log line from a container.</summary>
/// <param name="Timestamp">When the line was emitted (UTC).</param>
/// <param name="Source">Whether it came from stdout or stderr.</param>
/// <param name="Message">The log text (without trailing newline).</param>
public sealed record LogEntry(DateTimeOffset Timestamp, LogSource Source, string Message);

/// <summary>A point-in-time resource-usage sample for a container.</summary>
public sealed record ContainerStats
{
    /// <summary>Container the sample belongs to.</summary>
    public required string ContainerId { get; init; }

    /// <summary>CPU usage as a percentage (0–100 × cores).</summary>
    public double CpuPercent { get; init; }

    /// <summary>Memory currently used, in bytes.</summary>
    public long MemoryUsedBytes { get; init; }

    /// <summary>Memory limit, in bytes.</summary>
    public long MemoryLimitBytes { get; init; }

    /// <summary>Bytes received over the network since start.</summary>
    public long NetRxBytes { get; init; }

    /// <summary>Bytes transmitted over the network since start.</summary>
    public long NetTxBytes { get; init; }

    /// <summary>Bytes read from block devices since start.</summary>
    public long BlockReadBytes { get; init; }

    /// <summary>Bytes written to block devices since start.</summary>
    public long BlockWriteBytes { get; init; }

    /// <summary>Memory used as a fraction of the limit (0–1), or 0 when no limit.</summary>
    public double MemoryFraction =>
        MemoryLimitBytes > 0 ? (double)MemoryUsedBytes / MemoryLimitBytes : 0;
}

/// <summary>An event emitted by the engine about a resource.</summary>
/// <param name="Type">What happened.</param>
/// <param name="ResourceKind">The kind of resource affected.</param>
/// <param name="ResourceId">Id of the affected resource.</param>
/// <param name="Timestamp">When it happened (UTC).</param>
public sealed record EngineEvent(
    EngineEventType Type,
    ResourceKind ResourceKind,
    string ResourceId,
    DateTimeOffset Timestamp);
