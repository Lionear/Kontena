using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Adapters.Kubernetes;
using Kontena.Core.Models;
using Kontena.Core.Orchestration;
using Kontena.Core.Orchestration.Models;

namespace Kontena.App.ViewModels;

/// <summary>
/// What only exists in cluster mode (KON-65): namespaces, workload actions (KON-71), port
/// forwards, and the kubeconfig contexts that are known but not yet offered (KON-120).
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>The namespace filter, or null when "All namespaces" is selected.</summary>
    private string? ActiveNamespace => SelectedNamespace is null or AllNamespaces ? null : SelectedNamespace;
    // ── Workload actions (KON-71) ───────────────────────────────────────────

    private void ShowScaleDialog(Workload workload)
    {
        if (_cluster is null)
            return;

        Dialog = new ScaleWorkloadViewModel(_cluster, workload, CloseDialog, onDone: () =>
        {
            CloseDialog();
            ReloadCurrentClusterPage();
            return Task.CompletedTask;
        });
    }
    private void ConfirmRestartWorkload(Workload workload)
    {
        if (_cluster is null)
            return;

        Dialog = new ConfirmViewModel(
            "Restart rollout",
            $"Roll out a restart of {workload.Kind} \"{workload.Name}\" in {workload.Namespace}? Its pods are recreated" +
            " a few at a time so the workload stays available.",
            "Restart",
            onConfirm: async () =>
            {
                var reference = new ResourceRef(new GroupVersionKind("apps", "v1", workload.Kind.ToString()), workload.Namespace, workload.Name);
                await _cluster.RolloutRestartAsync(reference);
                CloseDialog();
                ReloadCurrentClusterPage();
            },
            onClose: CloseDialog);
    }
    /// <summary>Delete a pod (KON-69) — destructive, so it always goes through a confirm.</summary>
    private void ConfirmDeletePod(Pod pod)
    {
        if (_cluster is null)
            return;

        Dialog = new ConfirmViewModel(
            "Delete pod",
            $"Delete pod \"{pod.Name}\" in {pod.Namespace}? If a controller owns it, a replacement is" +
            " scheduled straight away; if not, it is gone for good.",
            "Delete",
            onConfirm: async () =>
            {
                await _cluster.DeleteAsync(new ResourceRef(GroupVersionKind.Pod, pod.Namespace, pod.Name));
                CloseDialog();
                ReloadCurrentClusterPage();
            },
            onClose: CloseDialog,
            destructive: true);
    }
    private void ShowServicePortForward(Service service)
    {
        if (_cluster is null)
            return;

        var reference = new ResourceRef(GroupVersionKind.Service, service.Namespace, service.Name);
        var ports = service.Ports.Select(p => p.Port).ToList();
        Dialog = new PortForwardViewModel(
            _portForwards, _cluster, reference, $"{service.Name} · {service.Namespace}", ports, CloseDialog,
            UpdatePortForwardCount);
    }
    private void ShowPodPortForward(Pod pod)
    {
        if (_cluster is null)
            return;

        var reference = new ResourceRef(GroupVersionKind.Pod, pod.Namespace, pod.Name);
        Dialog = new PortForwardViewModel(
            _portForwards, _cluster, reference, $"{pod.Name} · {pod.Namespace}", [], CloseDialog,
            UpdatePortForwardCount);
    }
    /// <summary>
    /// Badge the sidebar with the number of live tunnels — the whole point of the page is that they keep
    /// running while you are somewhere else — plus a marker when one fell over (KON-107).
    /// <para>
    /// The count stays "how many are working", which is why the last tunnel dropping takes the badge to
    /// nothing. That is correct and also unhelpful on its own: the page suddenly has something worth
    /// seeing and the nav says it is empty. So a dropped tunnel gets its own marker, and only a dropped
    /// one — paused and remembered rows are states the user chose, not events.
    /// </para>
    /// </summary>
    private void UpdatePortForwardCount()
    {
        SetNavCount("portforwards", _portForwards.ActiveCount == 0
            ? string.Empty
            : _portForwards.ActiveCount.ToString(CultureInfo.InvariantCulture));

        if (NavItems.FirstOrDefault(i => i.Key == "portforwards") is not { } item)
            return;

        var dropped = _portForwards.DroppedCount;
        item.NeedsAttention = dropped > 0;
        item.AttentionTip = dropped switch
        {
            0 => string.Empty,
            1 => "A port forward dropped",
            _ => $"{dropped} port forwards dropped",
        };
    }
    private void OnPortForwardsChanged()
    {
        UpdatePortForwardCount();
        RememberPortForwards();
    }
    /// <summary>
    /// Keep the current list for the backend it belongs to, so the next visit can offer it back
    /// (KON-105). Suspended while tearing a connection down: that clears the registry, and writing
    /// then would erase exactly the list we mean to keep.
    /// </summary>
    private void RememberPortForwards()
    {
        if (_suspendPortForwardMemory || string.IsNullOrEmpty(_activeBackend))
            return;

        var remembered = _portForwards.Snapshot();
        _settings = _store.Update(s =>
        {
            var all = new Dictionary<string, IReadOnlyList<RememberedPortForward>>(s.PortForwards);
            if (remembered.Count == 0)
                all.Remove(_activeBackend);
            else
                all[_activeBackend] = remembered;

            return s with { PortForwards = all };
        });
    }
    /// <summary>
    /// Tear the tunnels down without forgetting them: the list is written first, then cleared with
    /// persistence suspended, so leaving a cluster keeps what you had open on it.
    /// </summary>
    private async Task StopPortForwardsAsync()
    {
        RememberPortForwards();
        _suspendPortForwardMemory = true;
        try
        {
            await _portForwards.StopAllAsync();
        }
        finally
        {
            _suspendPortForwardMemory = false;
        }
    }
    /// <summary>Put back what this cluster had open last time, closed and waiting for a click.</summary>
    private void RestorePortForwards(IClusterEngine cluster, string backend)
    {
        if (_settings.PortForwards.TryGetValue(backend, out var remembered) && remembered.Count > 0)
            _portForwards.Restore(cluster, remembered);
    }
    partial void OnSelectedNamespaceChanged(string? value)
    {
        if (!IsClusterMode)
            return;

        // Reload the visible namespaced grid and refresh the nav counts.
        var key = NavItems.FirstOrDefault(i => i.IsSelected)?.Key ?? "overview";
        NavigateCluster(key);
        _ = UpdateClusterNavCountsAsync();
    }
    // ── New clusters (KON-120) ──────────────────────────────────────────────

    /// <summary>How many contexts are in a kubeconfig Kontena reads but have never been offered.</summary>
    [ObservableProperty] private int _newClusterCount;

    public bool HasNewClusters => NewClusterCount > 0;
    /// <summary>Reads as a sentence in the switcher, so the count and the noun agree.</summary>
    public string NewClusterNotice => NewClusterCount == 1
        ? "1 new cluster found"
        : $"{NewClusterCount} new clusters found";
    partial void OnNewClusterCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasNewClusters));
        OnPropertyChanged(nameof(NewClusterNotice));
    }
    /// <summary>
    /// Counts contexts that exist but have never been offered. Mentioned rather than added: a kubeconfig
    /// gains clusters that belong to other people, and appearing in the switcher by itself is how a
    /// production cluster ends up one click from a toy.
    /// </summary>
    private void RefreshNewClusters()
    {
        var settings = _store.Load();
        var discovered = BackendCatalog.DiscoverClusters(settings.KubeconfigPaths)
            .Select(p => p.Backend)
            .ToList();

        NewClusterCount = settings.NewClusters(discovered).Count;
    }
    /// <summary>
    /// The kubeconfigs Kontena reads (KON-122). The default one is listed but not removable: showing it
    /// answers "where are these clusters coming from" without offering to stop reading the file every
    /// kubectl user has.
    /// </summary>
    private IReadOnlyList<KubeconfigSource> Kubeconfigs()
    {
        var stored = _store.Load();
        return
        [
            new KubeconfigSource(string.Empty, Kubeconfig.DefaultPath, CanRemove: false),
            .. stored.KubeconfigPaths.Select(p => new KubeconfigSource(p, p, CanRemove: true)),
        ];
    }
    /// <summary>Every cluster in every kubeconfig Kontena reads, for the Settings list.</summary>
    private IReadOnlyList<DiscoveredCluster> DiscoveredClusters()
    {
        var paths = _store.Load().KubeconfigPaths;
        return
        [
            .. BackendCatalog.DiscoverClusters(paths)
                .Select(p => new DiscoveredCluster(
                    p.Backend,
                    p.DisplayName,

                    // Which file, not just "an added one": two kubeconfigs can both hold a context
                    // called "default", and then the path is the only thing telling the rows apart.
                    (p as KubernetesClusterProvider)?.KubeconfigPath ?? "default kubeconfig")),
        ];
    }
    /// <summary>The switcher's "n new clusters found" row — opens the wizard where they are chosen.</summary>
    [RelayCommand]
    private void ReviewNewClusters() => ShowAddBackend(AddBackendStep.Kubernetes);
    /// <summary>Open the pod-detail page for a pod (logs / shell / events / YAML).</summary>
    private void ShowPodDetail(Pod pod)
    {
        if (_cluster is null)
            return;

        DisposeDetail();

        var current = _store.Load();
        var font = new TerminalFont(current.TerminalFontFamily, current.TerminalFontSize, current.TerminalLigatures);

        _podDetail = new ClusterPodDetailViewModel(_cluster, pod, () => NavigateCluster("pods"), font, ShowPodPortForward);
        CurrentPage = _podDetail;
    }
}
