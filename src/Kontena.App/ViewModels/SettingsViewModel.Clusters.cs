using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Sdk.Models;
using Kontena.Core.Models;

namespace Kontena.App.ViewModels;

/// <summary>
/// Settings › Local clusters: which kubeconfigs are read (KON-122) and which contexts are
/// offered in the switcher (KON-120).
/// </summary>
public partial class SettingsViewModel
{
    // ── Kubeconfigs (KON-122) ───────────────────────────────────────────────

    /// <summary>The kubeconfigs Kontena reads: the default one, plus whatever was added.</summary>
    public ObservableCollection<KubeconfigSource> Kubeconfigs { get; } = [];

    public bool HasRemovableKubeconfigs => Kubeconfigs.Any(k => k.CanRemove);

    /// <summary>
    /// Stops reading a kubeconfig. The file is left alone — this is "no longer read", not "deleted",
    /// and a config Kontena did not create is not Kontena's to remove.
    /// </summary>
    [RelayCommand]
    private void RemoveKubeconfig(KubeconfigSource? source)
    {
        if (source is null || !source.CanRemove)
            return;

        // Confirmed, but not dressed up as data loss (KON-126): nothing on disk changes, so threatening
        // would teach people to click these away — and then the volume dialog stops working too.
        Confirm(
            "Stop reading this kubeconfig",
            $"Stop reading \"{source.Label}\"? The file stays exactly where it is; its clusters just no" +
            " longer appear in Kontena. You can add it back at any time.",
            "Stop reading",
            () => { RemoveKubeconfigCore(source); return Task.CompletedTask; },
            destructive: false);
    }

    private void RemoveKubeconfigCore(KubeconfigSource source)
    {
        _settings = _store.Update(s => s with
        {
            KubeconfigPaths = [.. s.KubeconfigPaths.Where(p => !string.Equals(p, source.Path, StringComparison.Ordinal))],
        });

        Kubeconfigs.Remove(source);
        OnPropertyChanged(nameof(HasRemovableKubeconfigs));

        // Its clusters go with it, along with their names and visibility — the rebuild prunes both.
        if (_onClustersChanged is not null)
            _ = _onClustersChanged();
    }

    // ── Clusters (KON-120) ──────────────────────────────────────────────────

    private readonly IReadOnlyList<DiscoveredCluster> _discoveredClusters;
    private readonly Func<Task>? _onClustersChanged;

    /// <summary>Every cluster in every kubeconfig Kontena reads, shown or not.</summary>
    public ObservableCollection<ClusterChoiceRow> Clusters { get; } = [];

    public bool HasClusters => Clusters.Count > 0;

    private void RefreshClusters()
    {
        Clusters.Clear();
        foreach (var cluster in _discoveredClusters)
            Clusters.Add(new ClusterChoiceRow(cluster, _settings.ShowsCluster(cluster.Backend), SetCluster));

        OnPropertyChanged(nameof(HasClusters));
    }

    /// <summary>
    /// Shows or hides a cluster, and rebuilds the backend list — a cluster that is not shown is not a
    /// provider, so it is not probed either. Which is the point: a cluster nobody asked for should not
    /// be contacted.
    /// </summary>
    private void SetCluster(string backend, bool shown)
    {
        _settings = _store.Update(s => s.WithCluster(backend, shown));

        if (_onClustersChanged is not null)
            _ = _onClustersChanged();
    }
}
