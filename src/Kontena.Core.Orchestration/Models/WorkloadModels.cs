namespace Kontena.Core.Orchestration.Models;

/// <summary>The controller kinds Kontena surfaces as "workloads".</summary>
public enum WorkloadKind
{
    Deployment,
    StatefulSet,
    DaemonSet,
    ReplicaSet,
    Job,
    CronJob,
}

/// <summary>Where a rollout stands, summarised for the grid and the detail header.</summary>
public enum RolloutStatus
{
    /// <summary>Desired replicas are ready and up to date.</summary>
    Complete,

    /// <summary>A rollout is in progress (new pods coming up / old draining).</summary>
    Progressing,

    /// <summary>Stuck or failing (e.g. image pull error, crash loop, unmet quota).</summary>
    Degraded,

    /// <summary>Deliberately scaled to zero.</summary>
    Paused,
}

/// <summary>
/// A neutral view of any workload controller (Deployment/StatefulSet/DaemonSet/Job/…). One
/// row type serves every kind; <see cref="Kind"/> tells the UI which it is, and the
/// ready/desired counts drive the health pill.
/// </summary>
public sealed record Workload
{
    public required string Name { get; init; }
    public required string Namespace { get; init; }
    public required WorkloadKind Kind { get; init; }

    /// <summary>Ready replicas / pods.</summary>
    public int Ready { get; init; }

    /// <summary>Desired replicas. For DaemonSets, desired scheduled; for Jobs, completions.</summary>
    public int Desired { get; init; }

    /// <summary>Up-to-date replicas (new revision).</summary>
    public int UpToDate { get; init; }

    /// <summary>Available replicas.</summary>
    public int Available { get; init; }

    /// <summary>Container images this workload runs (for the grid's image column).</summary>
    public IReadOnlyList<string> Images { get; init; } = [];

    /// <summary>Rollout health.</summary>
    public RolloutStatus RolloutStatus { get; init; } = RolloutStatus.Complete;

    /// <summary>For CronJobs — the schedule; empty otherwise.</summary>
    public string Schedule { get; init; } = string.Empty;

    /// <summary>Whether scaling applies (false for DaemonSets/Jobs/CronJobs).</summary>
    public bool IsScalable => Kind is WorkloadKind.Deployment or WorkloadKind.StatefulSet or WorkloadKind.ReplicaSet;

    public TimeSpan Age { get; init; }
}
