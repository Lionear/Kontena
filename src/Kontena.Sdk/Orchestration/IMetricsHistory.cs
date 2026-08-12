namespace Kontena.Sdk.Orchestration;

/// <summary>Which measure a history query is about.</summary>
public enum UsageMetric
{
    /// <summary>CPU in milli-cores.</summary>
    Cpu = 0,

    /// <summary>Memory working set in bytes.</summary>
    Memory,
}

/// <summary>One point in a usage series.</summary>
/// <param name="At">When it was measured (UTC).</param>
/// <param name="Value">Milli-cores or bytes, per the <see cref="UsageMetric"/> asked for.</param>
public readonly record struct UsageSample(DateTimeOffset At, double Value);

/// <summary>What a history query is about.</summary>
public enum UsageScope
{
    /// <summary>One pod.</summary>
    Pod = 0,

    /// <summary>Every pod a workload owns, including the ones a rollout has since replaced.</summary>
    Workload,

    /// <summary>Every pod in a namespace.</summary>
    Namespace,

    /// <summary>One node.</summary>
    Node,

    /// <summary>Everything running in the cluster, summed.</summary>
    Cluster,
}

/// <summary>
/// The thing whose usage is being asked for.
/// </summary>
/// <param name="Scope">Which kind of thing.</param>
/// <param name="Name">Its name.</param>
/// <param name="Namespace">Its namespace; null for cluster-scoped scopes.</param>
/// <param name="Kind">
/// For <see cref="UsageScope.Workload"/>, the workload kind — "Deployment", "StatefulSet",
/// "DaemonSet", "Job" or "CronJob". It decides how the pods are traced back to their owner, which
/// differs per kind: a Deployment owns its pods through a ReplicaSet and a CronJob through a Job.
/// </param>
public readonly record struct UsageTarget(
    UsageScope Scope, string Name, string? Namespace = null, string? Kind = null)
{
    public static UsageTarget Pod(string ns, string name) => new(UsageScope.Pod, name, ns);
    public static UsageTarget Namespaced(string ns) => new(UsageScope.Namespace, ns, ns);
    public static UsageTarget Node(string name) => new(UsageScope.Node, name);
    public static UsageTarget Cluster() => new(UsageScope.Cluster, "cluster");

    public static UsageTarget Workload(string ns, string name, string kind) =>
        new(UsageScope.Workload, name, ns, kind);
}

/// <summary>
/// A source that can answer for the <b>past</b>, which <see cref="IMetricsSource"/> deliberately
/// cannot (KON-345).
/// <para>
/// Separate interface rather than more methods on <see cref="IMetricsSource"/>, because the two are
/// different capabilities and clusters routinely have one without the other. A metrics-server
/// answers "now" and keeps nothing; a Prometheus keeps weeks and is not what the live gauges should
/// be polling at 15s. Kontena reads both where both exist.
/// </para>
/// </summary>
public interface IMetricsHistory
{
    /// <summary>Short name for the UI, e.g. "Prometheus".</summary>
    string Name { get; }

    /// <summary>
    /// Whether this source can currently answer. False makes the UI keep to the live buffer and
    /// leave the longer ranges disabled, rather than offering a range that returns nothing.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Look for the source and update <see cref="IsAvailable"/>. Must not throw: an absent or
    /// unreachable Prometheus is a false, not an error.
    /// </summary>
    ValueTask<bool> ProbeAsync(CancellationToken ct = default);

    /// <summary>
    /// Whether this source can answer for that kind of thing at all. Not every scope has a series
    /// behind it: container metrics roll up to pods, workloads and namespaces cleanly, while a
    /// node's own usage lives in a different exporter keyed by address rather than by node name.
    /// A page whose scope is unsupported keeps to the live buffer instead of offering ranges that
    /// come back empty.
    /// </summary>
    bool Supports(UsageScope scope);

    /// <summary>
    /// One target's usage over the last <paramref name="range"/>, oldest first. Empty when the
    /// source has nothing — a pod younger than the range, or a scrape that never saw it.
    /// <para>
    /// The resolution is the source's to choose: a range query is answered at whatever step keeps
    /// the series a sensible size, and the caller draws what it is given.
    /// </para>
    /// </summary>
    ValueTask<IReadOnlyList<UsageSample>> GetHistoryAsync(
        UsageTarget target, UsageMetric metric, TimeSpan range, CancellationToken ct = default);

    /// <summary>
    /// How often re-asking is worth it for this range. A 15-minute chart moves every scrape; a
    /// week-long one does not change meaningfully between coffees.
    /// </summary>
    TimeSpan RefreshInterval(TimeSpan range);
}

/// <summary>
/// Implemented by cluster backends that can answer for the past. Optional, like
/// <see cref="IMetricsAware"/>: a backend without it simply has no history, and the UI keeps to
/// what it sampled itself.
/// </summary>
public interface IMetricsHistoryAware
{
    /// <summary>Never null — an absent source is <see cref="NoMetricsHistory"/>.</summary>
    IMetricsHistory History { get; }
}

/// <summary>The null history: a cluster with nothing that remembers.</summary>
public sealed class NoMetricsHistory : IMetricsHistory
{
    public static readonly NoMetricsHistory Instance = new();

    public string Name => "none";
    public bool IsAvailable => false;

    public ValueTask<bool> ProbeAsync(CancellationToken ct = default) => ValueTask.FromResult(false);

    public bool Supports(UsageScope scope) => false;

    public ValueTask<IReadOnlyList<UsageSample>> GetHistoryAsync(
        UsageTarget target, UsageMetric metric, TimeSpan range, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<UsageSample>>([]);

    public TimeSpan RefreshInterval(TimeSpan range) => TimeSpan.FromMinutes(1);
}
