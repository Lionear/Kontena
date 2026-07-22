using Kontena.Core.Orchestration.Models;

namespace Kontena.Core.Orchestration;

/// <summary>
/// Where a cluster's usage numbers come from. Deliberately an abstraction rather than a hard
/// dependency on <c>metrics.k8s.io</c>: plenty of clusters run Prometheus instead of a
/// metrics-server, and some run neither.
/// <para>
/// Node <b>conditions</b> are not part of this — they come off the node status and are always
/// available. Only the usage gauges need a source, which is why
/// <see cref="ClusterCapabilities.Metrics"/> gates the gauges alone.
/// </para>
/// <para>
/// Implementations today: the metrics-server source in the Kubernetes adapter, and
/// <see cref="NoMetricsSource"/>. A Prometheus source is KON-84; auto-detecting which one a
/// cluster should use is KON-85.
/// </para>
/// </summary>
public interface IMetricsSource
{
    /// <summary>Short name for the UI, e.g. "metrics-server", "Prometheus", "none".</summary>
    string Name { get; }

    /// <summary>
    /// Whether this source can currently answer. False makes the UI hide usage gauges rather than
    /// show empty ones.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Probe the source and update <see cref="IsAvailable"/>. Called once when a cluster connects;
    /// it must not throw — an unreachable source is a false, not an error.
    /// </summary>
    ValueTask<bool> ProbeAsync(CancellationToken ct = default);

    /// <summary>Current usage per node, keyed by node name. Empty when unavailable.</summary>
    ValueTask<IReadOnlyDictionary<string, NodeUsage>> GetNodeUsageAsync(CancellationToken ct = default);

    /// <summary>Current usage for the pods in <paramref name="ns"/> (null = all namespaces).</summary>
    ValueTask<IReadOnlyList<PodMetrics>> GetPodUsageAsync(string? ns = null, CancellationToken ct = default);
}

/// <summary>
/// Implemented by cluster backends that resolve usage through a pluggable
/// <see cref="IMetricsSource"/>. Optional on purpose: <see cref="ClusterCapabilities.Metrics"/>
/// already tells the UI whether to draw gauges, and this only adds the detail needed to explain
/// <i>why</i> they are missing and what the user could do about it.
/// </summary>
public interface IMetricsAware
{
    /// <summary>The source usage is read from; never null — an absent source is
    /// <see cref="NoMetricsSource"/>.</summary>
    IMetricsSource Metrics { get; }
}

/// <summary>
/// The null source: a cluster with no usage backend. Conditions and every other read still work;
/// only the gauges go away.
/// </summary>
public sealed class NoMetricsSource : IMetricsSource
{
    public static readonly NoMetricsSource Instance = new();

    public string Name => "none";
    public bool IsAvailable => false;

    public ValueTask<bool> ProbeAsync(CancellationToken ct = default) => ValueTask.FromResult(false);

    public ValueTask<IReadOnlyDictionary<string, NodeUsage>> GetNodeUsageAsync(CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyDictionary<string, NodeUsage>>(new Dictionary<string, NodeUsage>());

    public ValueTask<IReadOnlyList<PodMetrics>> GetPodUsageAsync(string? ns = null, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<PodMetrics>>([]);
}
