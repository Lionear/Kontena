using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;
using Kontena.Core.Orchestration;

namespace Kontena.App.ViewModels;

/// <summary>
/// The cluster landing page: identity + resource counts + a compact node table, read from an
/// <see cref="IClusterEngine"/>. Where a cluster is summarised; the per-resource browsers it links
/// on to (nodes, pods, workloads, apply/dry-run) are their own pages, built in KON-73.
/// </summary>
public partial class ClusterOverviewViewModel : ViewModelBase
{
    private readonly IClusterEngine _cluster;

    public ClusterOverviewViewModel(IClusterEngine cluster)
    {
        _cluster = cluster;
        _ = LoadAsync();
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

    private async Task LoadAsync()
    {
        var info = await _cluster.GetInfoAsync();
        ClusterName = info.DisplayName;
        Version = info.Version;
        Distribution = info is ClusterInfo ci ? ci.Distribution : "cluster";

        var nodes = await _cluster.ListNodesAsync();
        NodeCount = nodes.Count;
        NamespaceCount = (await _cluster.ListNamespacesAsync()).Count;
        WorkloadCount = (await _cluster.ListWorkloadsAsync()).Count;
        PodCount = (await _cluster.ListPodsAsync()).Count;
        ServiceCount = (await _cluster.ListServicesAsync()).Count;

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
