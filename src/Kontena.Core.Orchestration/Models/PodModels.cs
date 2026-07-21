namespace Kontena.Core.Orchestration.Models;

/// <summary>A pod's lifecycle phase.</summary>
public enum PodPhase
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Unknown,
}

/// <summary>Quality-of-service class the scheduler assigned.</summary>
public enum QosClass
{
    BestEffort,
    Burstable,
    Guaranteed,
}

/// <summary>Per-container status inside a pod — drives the pod-detail container list.</summary>
public sealed record ContainerStatus
{
    public required string Name { get; init; }

    /// <summary>Image the container runs.</summary>
    public string Image { get; init; } = string.Empty;

    /// <summary>Whether the container currently passes its readiness probe.</summary>
    public bool Ready { get; init; }

    /// <summary>Restart count.</summary>
    public int Restarts { get; init; }

    /// <summary>State summary, e.g. "Running", "Waiting: CrashLoopBackOff", "Terminated: Error".</summary>
    public string State { get; init; } = string.Empty;
}

/// <summary>A neutral view of a pod, seeded richly enough to build the pods grid and detail.</summary>
public sealed record Pod
{
    public required string Name { get; init; }
    public required string Namespace { get; init; }

    public PodPhase Phase { get; init; } = PodPhase.Unknown;

    /// <summary>Containers and their per-container status.</summary>
    public IReadOnlyList<ContainerStatus> Containers { get; init; } = [];

    /// <summary>Total restarts across all containers.</summary>
    public int Restarts { get; init; }

    /// <summary>Node the pod is scheduled on.</summary>
    public string Node { get; init; } = string.Empty;

    /// <summary>Pod IP, when assigned.</summary>
    public string Ip { get; init; } = string.Empty;

    /// <summary>Scheduler QoS class.</summary>
    public QosClass Qos { get; init; } = QosClass.BestEffort;

    /// <summary>Owning controller, e.g. "Deployment/api" — empty for bare pods.</summary>
    public string ControlledBy { get; init; } = string.Empty;

    /// <summary>Ready containers / total containers (for the "2/2" column).</summary>
    public int ReadyContainers => Containers.Count(c => c.Ready);

    public TimeSpan Age { get; init; }
}

/// <summary>
/// Live pod resource usage from the metrics-server — the OAL mirror of the CEAL's
/// <c>ContainerStats</c>. CPU in milli-cores, memory in bytes.
/// </summary>
public sealed record PodMetrics
{
    public required string Pod { get; init; }
    public required string Namespace { get; init; }

    /// <summary>CPU usage in milli-cores.</summary>
    public long CpuMillicores { get; init; }

    /// <summary>Memory working set in bytes.</summary>
    public long MemoryBytes { get; init; }

    /// <summary>When the sample was taken (UTC).</summary>
    public DateTimeOffset Timestamp { get; init; }
}
