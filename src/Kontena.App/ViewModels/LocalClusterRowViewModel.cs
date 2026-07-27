using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Orchestration.Provisioning;

namespace Kontena.App.ViewModels;

/// <summary>One local cluster in the list (KON-76, KON-77).</summary>
public sealed partial class LocalClusterRowViewModel(
    LocalCluster cluster,
    bool isActive,
    ProvisionerCapabilities capabilities,
    Func<LocalClusterRowViewModel, Task> use,
    Func<LocalClusterRowViewModel, Task> delete,
    Func<LocalClusterRowViewModel, Task> start,
    Func<LocalClusterRowViewModel, Task> stop) : ObservableObject
{
    public LocalCluster Cluster => cluster;

    public string Name => cluster.Name;

    /// <summary>The kubeconfig context — the join between this row and the switcher entry.</summary>
    public string Context => cluster.Context;

    /// <summary>True when this cluster is the backend Kontena is talking to right now.</summary>
    public bool IsActive { get; } = isActive;

    public bool IsStopped => cluster.State == LocalClusterState.Stopped;

    /// <summary>
    /// A stopped cluster is not something to connect to; starting it is the step before that. Switching
    /// to it would present an apiserver that is not listening as a broken backend.
    /// </summary>
    public bool CanUse => !IsActive && !IsStopped;

    /// <summary>Only where the tool has a stopped state at all — kind does not (KON-77).</summary>
    public bool CanStart => capabilities.StartStop && IsStopped;

    public bool CanStop => capabilities.StartStop && cluster.State == LocalClusterState.Running;

    /// <summary>Whether to show a state at all. "Unknown" is not worth a word of its own.</summary>
    public bool HasState => cluster.State != LocalClusterState.Unknown;

    public string StateText => cluster.State switch
    {
        LocalClusterState.Running => "Running",
        LocalClusterState.Stopped => "Stopped",
        _ => string.Empty,
    };

    /// <summary>
    /// Split in two so the view can pick a brush without a converter — and both carry the word, because
    /// colour is never the only thing that says it (KON-56).
    /// </summary>
    public bool IsRunning => cluster.State == LocalClusterState.Running;

    /// <summary>
    /// What is known for certain: who made it, what it runs on, and how many nodes it has.
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

            if (!string.IsNullOrWhiteSpace(cluster.Driver))
                parts.Add(cluster.Driver);

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

    [RelayCommand]
    private Task Start() => start(this);

    [RelayCommand]
    private Task Stop() => stop(this);
}
