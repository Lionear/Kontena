using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Kontena.App.Controls;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;
using Kontena.Core.Orchestration;

namespace Kontena.App.ViewModels;

/// <summary>
/// The cluster landing page: identity + resource counts + a compact node table, read from an
/// <see cref="IClusterEngine"/>. Where a cluster is summarised; the per-resource browsers it links
/// on to (nodes, pods, workloads, apply/dry-run) are their own pages, built in KON-73.
/// </summary>
public partial class ClusterOverviewViewModel : ViewModelBase, IDisposable
{
    private readonly IClusterEngine _cluster;
    private CancellationTokenSource? _usage;

    public ClusterOverviewViewModel(IClusterEngine cluster)
    {
        _cluster = cluster;

        // The page you land on, and until now the only one that could not answer "is the cluster
        // busy" (KON-347). Summed over pods rather than over nodes, so it agrees with the namespace
        // and workload charts instead of being a fourth number nobody can reconcile.
        if (cluster.Capabilities.Metrics)
        {
            Usage = new UsageTrackViewModel(
                [
                    new UsageChartSpec("CPU", UsageChartUnit.Millicores, "Primary", UsageMetric.Cpu,
                        "millicores across every pod"),
                    new UsageChartSpec("Memory", UsageChartUnit.Bytes, "Accent", UsageMetric.Memory,
                        "working set across every pod"),
                ],
                UsageTarget.Cluster(),
                cluster is IMetricsHistoryAware historyAware ? historyAware.History : null,
                cluster is IMetricsAware metricsAware ? metricsAware.Metrics.Name : "the metrics source");

            _usage = new CancellationTokenSource();
            _ = Usage.ProbeAsync(_usage.Token);
            _ = PollUsageAsync(_usage.Token);
        }

        _ = LoadAsync();
    }

    /// <summary>Cluster-wide usage, or null where the cluster has no metrics source.</summary>
    public UsageTrackViewModel? Usage { get; }

    public bool ShowUsageGraphs => Usage is not null;

    private async Task PollUsageAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_cluster is IMetricsAware aware)
                {
                    var pods = await aware.Metrics.GetPodUsageAsync(null, ct).ConfigureAwait(true);
                    if (pods.Count > 0 && Usage is { } usage)
                    {
                        usage.Add(
                            DateTimeOffset.UtcNow,
                            pods.Sum(p => (double)p.CpuMillicores),
                            pods.Sum(p => (double)p.MemoryBytes));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // One failed read is a gap, not the end of the charts.
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>The poll outlives nothing: cluster pages are rebuilt on every visit.</summary>
    public void Dispose()
    {
        _usage?.Cancel();
        _usage?.Dispose();
        _usage = null;
        GC.SuppressFinalize(this);
    }

    [ObservableProperty] private string _clusterName = string.Empty;
    [ObservableProperty] private string _distribution = string.Empty;
    [ObservableProperty] private string _version = string.Empty;

    [ObservableProperty] private int _nodeCount;
    [ObservableProperty] private int _namespaceCount;
    [ObservableProperty] private int _workloadCount;
    [ObservableProperty] private int _podCount;
    [ObservableProperty] private int _serviceCount;

    public ObservableCollection<NodeRow> Nodes { get; } = [];

    /// <summary>
    /// Six reads that know nothing of each other, so all six are started before any is awaited
    /// (KON-338). Sequentially they were six round-trips deep, and this page is the first thing a
    /// cluster shows — on a remote one that wait is the whole first impression.
    /// </summary>
    private async Task LoadAsync()
    {
        var infoTask = _cluster.GetInfoAsync().AsTask();
        var nodesTask = _cluster.ListNodesAsync().AsTask();
        var namespacesTask = _cluster.ListNamespacesAsync().AsTask();
        var workloadsTask = _cluster.ListWorkloadsAsync().AsTask();
        var podsTask = _cluster.ListPodsAsync().AsTask();
        var servicesTask = _cluster.ListServicesAsync().AsTask();

        await Task.WhenAll(infoTask, nodesTask, namespacesTask, workloadsTask, podsTask, servicesTask);

        var info = infoTask.Result;
        ClusterName = info.DisplayName;
        Version = info.Version;
        Distribution = info is ClusterInfo ci ? ci.Distribution : "cluster";

        var nodes = nodesTask.Result;
        NodeCount = nodes.Count;
        NamespaceCount = namespacesTask.Result.Count;
        WorkloadCount = workloadsTask.Result.Count;
        PodCount = podsTask.Result.Count;
        ServiceCount = servicesTask.Result.Count;

        Nodes.Clear();
        foreach (var n in nodes)
            Nodes.Add(new NodeRow(
                n.Name,
                n.Roles.Count > 0 ? string.Join(", ", n.Roles) : "—",
                n.Status,
                n.KubeletVersion,
                n.Usage is null ? "—" : $"{n.Usage.CpuMillicores}m / {n.Capacity.CpuMillicores}m",
                VersionSkewPolicy.Evaluate(info.Version, n.KubeletVersion)));
    }
}

/// <summary>A row in the overview's node table.</summary>
public sealed record NodeRow(string Name, string Roles, string Status, string Version, string Cpu, NodeVersionSkew? Skew = null)
{
    /// <summary>A kubelet outside the supported skew window (KON-95) — the version alone doesn't show it.</summary>
    public bool HasVersionWarning => Skew?.IsProblem == true;

    public string VersionWarningDetail => Skew?.Detail ?? string.Empty;
}
