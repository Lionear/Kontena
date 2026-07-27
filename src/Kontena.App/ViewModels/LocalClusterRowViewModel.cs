using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Orchestration.Provisioning;

namespace Kontena.App.ViewModels;

/// <summary>One local cluster in the list (KON-76).</summary>
public sealed partial class LocalClusterRowViewModel(
    LocalCluster cluster,
    bool isActive,
    Func<LocalClusterRowViewModel, Task> use,
    Func<LocalClusterRowViewModel, Task> delete) : ObservableObject
{
    public LocalCluster Cluster => cluster;

    public string Name => cluster.Name;

    /// <summary>The kubeconfig context — the join between this row and the switcher entry.</summary>
    public string Context => cluster.Context;

    /// <summary>True when this cluster is the backend Kontena is talking to right now.</summary>
    public bool IsActive { get; } = isActive;

    public bool CanUse => !IsActive;

    /// <summary>
    /// What is known for certain: who made it, and how many nodes it has.
    /// <para>
    /// Not the Kubernetes version, the ports or the ingress label. kind does not report those for an
    /// existing cluster, and showing them only for the one you just made would put two kinds of row in
    /// one list — where the fuller one looks like the truth and the other looks broken.
    /// </para>
    /// </summary>
    public string Facts
    {
        get
        {
            var parts = new List<string> { cluster.Provisioner };
            if (cluster.Nodes.Count > 0)
            {
                parts.Add(cluster.Nodes.Count == 1
                    ? "1 node"
                    : string.Create(CultureInfo.InvariantCulture, $"{cluster.Nodes.Count} nodes"));
            }

            return string.Join(" · ", parts);
        }
    }

    [RelayCommand]
    private Task Use() => use(this);

    [RelayCommand]
    private Task Delete() => delete(this);
}
