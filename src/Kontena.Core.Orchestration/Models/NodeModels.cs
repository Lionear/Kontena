namespace Kontena.Core.Orchestration.Models;

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

    /// <summary>Allocatable capacity.</summary>
    public NodeCapacity Capacity { get; init; } = new();

    /// <summary>Live usage from the metrics-server; null when metrics are unavailable.</summary>
    public NodeUsage? Usage { get; init; }

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
}

/// <summary>A node's live resource usage, sampled from the metrics-server.</summary>
public sealed record NodeUsage
{
    /// <summary>Used CPU in milli-cores.</summary>
    public long CpuMillicores { get; init; }

    /// <summary>Used memory in bytes.</summary>
    public long MemoryBytes { get; init; }

    /// <summary>Currently scheduled pods.</summary>
    public int Pods { get; init; }
}
