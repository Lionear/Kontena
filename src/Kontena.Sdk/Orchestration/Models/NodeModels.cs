namespace Kontena.Sdk.Orchestration.Models;

/// <summary>
/// One reported node condition — <c>Ready</c>, <c>DiskPressure</c>, <c>MemoryPressure</c>,
/// <c>PIDPressure</c>, <c>NetworkUnavailable</c>. Conditions come straight off the node status, so
/// unlike the usage gauges they need no metrics source at all: a cluster without a metrics-server
/// still reports node health.
/// </summary>
/// <param name="Type">Condition name, e.g. "Ready" or "DiskPressure".</param>
/// <param name="IsActive">Whether the condition is currently true.</param>
/// <param name="Reason">Short machine reason, e.g. "KubeletReady"; empty when not reported.</param>
/// <param name="Message">Human-readable detail; empty when not reported.</param>
public readonly record struct NodeCondition(string Type, bool IsActive, string Reason, string Message)
{
    /// <summary>
    /// Whether this condition indicates a problem. <c>Ready</c> is healthy when true; every
    /// pressure/unavailable condition is the other way round.
    /// </summary>
    public bool IsProblem => Type == "Ready" ? !IsActive : IsActive;
}

/// <summary>A cluster node with its capacity and (when metrics are available) live usage.</summary>
public sealed record Node
{
    public required string Name { get; init; }

    /// <summary>"Ready", "NotReady", "Unknown" — the summarised Ready condition.</summary>
    public string Status { get; init; } = "Unknown";

    /// <summary>Roles, e.g. "control-plane", "worker".</summary>
    public IReadOnlyList<string> Roles { get; init; } = [];

    /// <summary>Kubelet version, e.g. "v1.29.4".</summary>
    public string KubeletVersion { get; init; } = string.Empty;

    /// <summary>OS image / container runtime string, for the detail panel.</summary>
    public string OsImage { get; init; } = string.Empty;

    /// <summary>Internal IP address.</summary>
    public string InternalIp { get; init; } = string.Empty;

    /// <summary>Whether the node is cordoned (scheduling disabled).</summary>
    public bool Unschedulable { get; init; }

    /// <summary>
    /// Reported conditions. Available from any cluster, with or without a metrics source — these
    /// drive the Nodes view's status indicators.
    /// </summary>
    public IReadOnlyList<NodeCondition> Conditions { get; init; } = [];

    /// <summary>Conditions currently signalling trouble (a failing Ready, or any pressure).</summary>
    public IReadOnlyList<NodeCondition> Problems => [.. Conditions.Where(c => c.IsProblem)];

    /// <summary>Allocatable capacity.</summary>
    public NodeCapacity Capacity { get; init; } = new();

    /// <summary>Live usage from the metrics source; null when no source is available.</summary>
    public NodeUsage? Usage { get; init; }

    /// <summary>
    /// Pods currently scheduled on this node. Counted from the pod list, not from a metrics source,
    /// so it stays available on a cluster that has no metrics-server.
    /// </summary>
    public int ScheduledPods { get; init; }

    public TimeSpan Age { get; init; }
}

/// <summary>A node's allocatable capacity. CPU in milli-cores, memory in bytes.</summary>
public sealed record NodeCapacity
{
    /// <summary>Allocatable CPU in milli-cores (1000m = 1 core).</summary>
    public long CpuMillicores { get; init; }

    /// <summary>Allocatable memory in bytes.</summary>
    public long MemoryBytes { get; init; }

    /// <summary>Maximum schedulable pods.</summary>
    public int Pods { get; init; }

    /// <summary>
    /// Node filesystem capacity in bytes. Only a source that reports disk fills this in — a
    /// metrics-server does not report disk at all — so zero means "unknown", not "no disk".
    /// </summary>
    public long DiskBytes { get; init; }
}

/// <summary>
/// A node's live resource usage, sampled from the active metrics source. Pod counts deliberately
/// live on <see cref="Node.ScheduledPods"/> instead — they need no metrics source.
/// </summary>
public sealed record NodeUsage
{
    /// <summary>Used CPU in milli-cores.</summary>
    public long CpuMillicores { get; init; }

    /// <summary>Used memory in bytes.</summary>
    public long MemoryBytes { get; init; }

    /// <summary>
    /// Used bytes on the node filesystem, or null when the active source does not report disk.
    /// Distinct from the <c>DiskPressure</c> condition, which is a threshold flag with no number.
    /// </summary>
    public long? DiskUsedBytes { get; init; }
}
