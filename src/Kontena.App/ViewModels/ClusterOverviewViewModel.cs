using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Kontena.Core.Orchestration;
using Kontena.Core.Orchestration.Models;

namespace Kontena.App.ViewModels;

/// <summary>
/// The cluster landing page: identity + resource counts + a compact node table, read from an
/// <see cref="IClusterEngine"/>. A first, real OAL view; the full per-resource browsers
/// (nodes, pods, workloads, apply/dry-run) land in KON-73.
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
                n.Usage is null ? "—" : $"{n.Usage.CpuMillicores}m / {n.Capacity.CpuMillicores}m"));
    }
}

/// <summary>A row in the overview's node table.</summary>
public sealed record NodeRow(string Name, string Roles, string Status, string Version, string Cpu);
