using k8s;
using Kontena.Core.Orchestration;
using Kontena.Core.Orchestration.Models;

namespace Kontena.Adapters.Kubernetes;

/// <summary>
/// Usage numbers from <c>metrics.k8s.io</c> — the metrics-server, the default source. Many clusters
/// (kind and bare kubeadm among them) ship without one, so <see cref="ProbeAsync"/> decides once
/// whether the gauges are available at all and the UI adapts rather than showing empty dials.
/// A Prometheus source is KON-84; picking between them automatically is KON-85.
/// </summary>
internal sealed class MetricsServerSource(IKubernetes client) : IMetricsSource
{
    public string Name => "metrics-server";
    public bool IsAvailable { get; private set; }

    public async ValueTask<bool> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            await client.GetKubernetesNodesMetricsAsync().ConfigureAwait(false);
            IsAvailable = true;
        }
        catch (Exception)
        {
            // No metrics-server, no permission, or the API is not registered — all mean "no gauges".
            IsAvailable = false;
        }

        return IsAvailable;
    }

    public async ValueTask<IReadOnlyDictionary<string, NodeUsage>> GetNodeUsageAsync(CancellationToken ct = default)
    {
        if (!IsAvailable)
            return new Dictionary<string, NodeUsage>();

        try
        {
            var metrics = await client.GetKubernetesNodesMetricsAsync().ConfigureAwait(false);
            var usage = new Dictionary<string, NodeUsage>(StringComparer.Ordinal);
            foreach (var m in metrics.Items ?? [])
            {
                if (m.Metadata?.Name is { } name)
                    usage[name] = K8sMap.ToNodeUsage(m);
            }

            return usage;
        }
        catch (Exception)
        {
            return new Dictionary<string, NodeUsage>();
        }
    }

    public async ValueTask<IReadOnlyList<Core.Orchestration.Models.PodMetrics>> GetPodUsageAsync(
        string? ns = null, CancellationToken ct = default)
    {
        if (!IsAvailable)
            return [];

        try
        {
            var metrics = ns is null
                ? await client.GetKubernetesPodsMetricsAsync().ConfigureAwait(false)
                : await client.GetKubernetesPodsMetricsByNamespaceAsync(ns).ConfigureAwait(false);

            return [.. (metrics.Items ?? []).Select(K8sMap.ToPodMetrics)];
        }
        catch (Exception)
        {
            return [];
        }
    }
}
