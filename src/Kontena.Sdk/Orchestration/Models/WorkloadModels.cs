namespace Kontena.Sdk.Orchestration.Models;

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

    /// <summary>Labels on the workload object itself (KON-166).</summary>
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// The pod selector. A CronJob has none — it does not own pods directly, its Jobs do — so an
    /// empty selector there is a fact about the kind rather than a gap in the mapping.
    /// </summary>
    public IReadOnlyDictionary<string, string> Selector { get; init; } = new Dictionary<string, string>();

    /// <summary>Update strategy, e.g. "RollingUpdate (max surge 25%, max unavailable 25%)".</summary>
    public string Strategy { get; init; } = string.Empty;

    /// <summary>This workload as an addressable reference — for the manifest tab and delete.</summary>
    public ResourceRef Reference => new(GroupVersionKind.For(Kind), Namespace, Name);

    public TimeSpan Age { get; init; }
}

/// <summary>
/// A HorizontalPodAutoscaler (KON-477), as far as a workload's detail page needs it: which workload it
/// scales, its bounds, where it stands, and what it scales on.
/// </summary>
public sealed record HorizontalPodAutoscaler
{
    public required string Name { get; init; }
    public required string Namespace { get; init; }

    /// <summary>The scale target's kind, e.g. "Deployment".</summary>
    public required string TargetKind { get; init; }
    public required string TargetName { get; init; }

    /// <summary>Kubernetes defaults this to 1 when the spec leaves it out.</summary>
    public int MinReplicas { get; init; } = 1;
    public int MaxReplicas { get; init; }
    public int CurrentReplicas { get; init; }
    public int DesiredReplicas { get; init; }

    /// <summary>
    /// Each metric as "what: current / target", e.g. "cpu: 45% / 70%". Current reads "?" until the
    /// autoscaler has one — a freshly created HPA, or one whose metrics source is missing.
    /// </summary>
    public IReadOnlyList<string> Metrics { get; init; } = [];

    public TimeSpan Age { get; init; }
}

/// <summary>
/// A PodDisruptionBudget (KON-477): how much voluntary disruption — a drain, an eviction — the pods it
/// selects will tolerate, and how much of that is left right now.
/// </summary>
public sealed record PodDisruptionBudget
{
    public required string Name { get; init; }
    public required string Namespace { get; init; }

    /// <summary>A count or a percentage, as written; null when the budget uses the other field.</summary>
    public string? MinAvailable { get; init; }

    /// <inheritdoc cref="MinAvailable"/>
    public string? MaxUnavailable { get; init; }

    /// <summary>
    /// Null is a budget without a selector, which selects no pods (policy/v1); an empty one selects
    /// every pod in the namespace.
    /// </summary>
    public LabelSelector? Selector { get; init; }

    public int CurrentHealthy { get; init; }
    public int DesiredHealthy { get; init; }
    public int ExpectedPods { get; init; }

    /// <summary>How many of its pods may be evicted right now. Zero is what blocks a drain.</summary>
    public int DisruptionsAllowed { get; init; }

    public TimeSpan Age { get; init; }
}
