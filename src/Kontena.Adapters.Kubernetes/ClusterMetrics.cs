using Kontena.Core.Orchestration;
using Kontena.Core.Orchestration.Models;

namespace Kontena.Adapters.Kubernetes;

/// <summary>
/// A metrics source that also reports node filesystem capacity — the denominator a disk gauge
/// needs, which the typed node listing does not carry. Only the kubelet source can do this;
/// keeping it a separate interface lets the composite be tested without a cluster.
/// </summary>
internal interface INodeDiskSource : IMetricsSource
{
    ValueTask<IReadOnlyDictionary<string, long>> GetNodeDiskCapacityAsync(CancellationToken ct = default);
}

/// <summary>
/// Combines the two sources a Kubernetes cluster can offer, because neither is sufficient alone.
/// <list type="bullet">
/// <item>A metrics-server answers for the whole cluster in one call, but reports <b>no disk</b>.</item>
/// <item>The kubelet summary reports disk, and works where no metrics-server exists — at one
/// request per node, and only with <c>nodes/proxy</c> permission.</item>
/// </list>
/// So: metrics-server for CPU and memory when present, the kubelet for disk always, and the kubelet
/// standing in for CPU and memory when there is no metrics-server. <see cref="Name"/> reports what
/// actually answered, which is what the UI shows when it has to explain missing gauges.
/// </summary>
internal sealed class ClusterMetrics(IMetricsSource metricsServer, INodeDiskSource kubelet) : IMetricsSource
{
    private Task<bool>? _probe;

    /// <summary>
    /// Probe once, on demand, and share the result. Callers are not required to have pinged first —
    /// relying on the UI to call things in the right order would make the adapter quietly wrong when
    /// used any other way.
    /// </summary>
    private Task<bool> EnsureProbedAsync(CancellationToken ct) =>
        _probe ??= ProbeAsync(ct).AsTask();

    public string Name => (metricsServer.IsAvailable, kubelet.IsAvailable) switch
    {
        (true, true) => "metrics-server + kubelet",
        (true, false) => "metrics-server",
        (false, true) => "kubelet",
        _ => "none",
    };

    public bool IsAvailable => metricsServer.IsAvailable || kubelet.IsAvailable;

    /// <summary>Whether disk figures are available — only the kubelet reports them.</summary>
    public bool HasDisk => kubelet.IsAvailable;

    public async ValueTask<bool> ProbeAsync(CancellationToken ct = default)
    {
        // Probe both: the answer is not "which one", it is "which parts do we get".
        await metricsServer.ProbeAsync(ct).ConfigureAwait(false);
        await kubelet.ProbeAsync(ct).ConfigureAwait(false);
        return IsAvailable;
    }

    public async ValueTask<IReadOnlyDictionary<string, NodeUsage>> GetNodeUsageAsync(CancellationToken ct = default)
    {
        await EnsureProbedAsync(ct).ConfigureAwait(false);

        var kubeletUsage = kubelet.IsAvailable
            ? await kubelet.GetNodeUsageAsync(ct).ConfigureAwait(false)
            : new Dictionary<string, NodeUsage>();

        if (!metricsServer.IsAvailable)
            return kubeletUsage;

        var authoritative = await metricsServer.GetNodeUsageAsync(ct).ConfigureAwait(false);
        if (kubeletUsage.Count == 0)
            return authoritative;

        // Prefer the metrics-server's CPU/memory (cluster-wide and consistent), keep the kubelet's disk.
        var merged = new Dictionary<string, NodeUsage>(StringComparer.Ordinal);
        foreach (var (node, usage) in authoritative)
            merged[node] = usage with { DiskUsedBytes = kubeletUsage.GetValueOrDefault(node)?.DiskUsedBytes };

        foreach (var (node, usage) in kubeletUsage)
            merged.TryAdd(node, usage);

        return merged;
    }

    /// <summary>Node filesystem capacity, so a disk gauge has a denominator. Empty without the kubelet.</summary>
    public async ValueTask<IReadOnlyDictionary<string, long>> GetNodeDiskCapacityAsync(CancellationToken ct = default)
    {
        await EnsureProbedAsync(ct).ConfigureAwait(false);

        // Guard here rather than trusting each source to check itself — the composite decides who
        // may answer, and an unavailable source answering anyway would light up a bogus gauge.
        return kubelet.IsAvailable
            ? await kubelet.GetNodeDiskCapacityAsync(ct).ConfigureAwait(false)
            : new Dictionary<string, long>();
    }

    public async ValueTask<IReadOnlyList<Core.Orchestration.Models.PodMetrics>> GetPodUsageAsync(
        string? ns = null, CancellationToken ct = default)
    {
        await EnsureProbedAsync(ct).ConfigureAwait(false);
        if (metricsServer.IsAvailable)
            return await metricsServer.GetPodUsageAsync(ns, ct).ConfigureAwait(false);

        return kubelet.IsAvailable ? await kubelet.GetPodUsageAsync(ns, ct).ConfigureAwait(false) : [];
    }
}
