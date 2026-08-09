using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Adapters.Kubernetes;
using Kontena.Sdk.Models;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;
using Kontena.Core.Models;
using Kontena.Core.Orchestration;
using Kontena.Core.Shell;

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
                await _cluster.RolloutRestartAsync(workload.Reference);
                CloseDialog();

                // A restart changes the workload's pods, not its identity — if this is the drawer the
                // user just clicked Restart from, refresh its pods tab in place rather than closing it
                // out from under them via the blanket page rebuild (KON-323).
                if (Detail is ClusterWorkloadDetailViewModel detail && detail.DetailKey == workload.Reference.ToString())
                    _ = detail.RefreshPodsAsync();
                else
                    ReloadCurrentClusterPage();
            },
            onClose: CloseDialog);
    }
    /// <summary>Delete a pod (KON-69) — destructive, so it always goes through a confirm.</summary>
    private void ConfirmDeletePod(Pod pod) =>
        ConfirmDeleteObject(
            new ResourceRef(GroupVersionKind.Pod, pod.Namespace, pod.Name), pod,
            "Delete pod",
            $"Delete pod \"{pod.Name}\" in {pod.Namespace}? If a controller owns it, a replacement is"
            + " scheduled straight away; if not, it is gone for good.");

    /// <summary>Delete a workload from its detail page (KON-334); the list row has its own (KON-332).</summary>
    private void ConfirmDeleteWorkload(Workload workload)
    {
        var (title, message) = ClusterDeleteWording.Workload(
            workload.Kind.ToString(), workload.Name, workload.Namespace);

        ConfirmDeleteObject(workload.Reference, workload, title, message);
    }

    /// <summary>Delete a service from its detail page (KON-334).</summary>
    private void ConfirmDeleteService(Service service)
    {
        var (title, message) = ClusterDeleteWording.Service(
            service.Name, service.Namespace, service.Type == ServiceType.LoadBalancer);

        ConfirmDeleteObject(
            new ResourceRef(GroupVersionKind.Service, service.Namespace, service.Name),
            service, title, message);
    }

    /// <summary>Delete a config map or secret from its detail page (KON-334).</summary>
    private void ConfirmDeleteConfigObject(ConfigObjectRow row)
    {
        var (title, message) = ConfigDelete.Words(row);

        ConfirmDeleteObject(row.Reference, row, title, message);
    }

    /// <summary>
    /// Delete one object, from wherever the shell was asked to (KON-334).
    /// <para>
    /// This is what a detail page's Delete needs that a list row's does not, and why these do not go
    /// through the page's own confirm the way the list pages do (KON-332): the history step that
    /// leads back to the object has to be dropped, and that is not the page's to do.
    /// </para>
    /// <para>
    /// Nothing here closes the detail, which the delete plainly has to do — <see cref="ReloadCurrentClusterPage"/>
    /// already does it, because rebuilding a cluster page starts by closing the drawer and by replacing
    /// whatever <c>CurrentPage</c> was. A second close alongside it was written first and removed: it
    /// passed every test with it commented out, which is the definition of a line that is not doing
    /// the work it claims. The behaviour is pinned by <c>DeleteFromDetailTests</c> either way, so a
    /// reload that stops closing details fails there rather than silently leaving a page up.
    /// </para>
    /// </summary>
    private void ConfirmDeleteObject(ResourceRef reference, object target, string title, string message)
    {
        if (_cluster is null)
            return;

        ConfirmDelete(title, message, async () =>
        {
            await _cluster.DeleteAsync(reference);

            // Back must not lead to the detail page of something that no longer exists — and only
            // this moment knows the step was ever valid (KON-173).
            ForgetSteps(target);
            ReloadCurrentClusterPage();
        });
    }
    /// <summary>
    /// The node-detail page (KON-197). Until this existed a node was a dead end: the card summarised
    /// its conditions to a chip and there was nowhere to read them in full, nor to see what was
    /// actually running on it.
    /// </summary>
    private async void ShowNodeDetail(Node node)
    {
        if (_cluster is null)
            return;

        // The apiserver version is what a kubelet version means anything against (KON-95), and a
        // failed lookup costs the warning rather than the page.
        var apiServer = string.Empty;
        try
        {
            apiServer = (await _cluster.GetInfoAsync()).Version;
        }
        catch (Exception)
        {
            // No version, no skew warning; everything else on the page stands.
        }

        ShowDetail(new ClusterNodeDetailViewModel(
            _cluster, node, apiServer,
            onOpenPod: ShowPodDetail,
            onCordon: (name, cordoned) => _cluster.CordonNodeAsync(name, cordoned).AsTask(),
            onDrain: ShowDrainNode),
            $"node {node.Name}", node);
    }

    /// <summary>
    /// The namespace-detail page (KON-197). The list answered "does it exist"; this answers what is
    /// in it — which is the question you had, and the one that decides whether it can go.
    /// </summary>
    private void ShowNamespaceDetail(KubeNamespace ns)
    {
        if (_cluster is null)
            return;

        ShowDetail(new ClusterNamespaceDetailViewModel(
            _cluster, ns,
            onOpenPod: ShowPodDetail,
            onOpenKind: OpenKindInNamespace),
            $"namespace {ns.Name}", ns);
    }

    /// <summary>
    /// Go to a list page with the namespace picker moved to the namespace you came from. Setting the
    /// picker rather than passing a filter keeps one source of truth for "which namespace am I in" —
    /// a page filtered to something the picker disagrees with is a page nobody can read.
    /// </summary>
    private void OpenKindInNamespace(string key, string ns)
    {
        SelectedNamespace = ns;
        Navigate(key);
    }

    /// <summary>
    /// The drain modal (KON-251). A dialog rather than something on the page, because a drain runs
    /// for as long as its pods take to go and the page underneath it is rebuilt on every visit.
    /// </summary>
    private void ShowDrainNode(string node)
    {
        if (_cluster is null)
            return;

        Dialog = new DrainNodeViewModel(_cluster, node, CloseDialog, onDone: () =>
        {
            // The node list is what changed — pod counts and the cordoned marker — and the dialog
            // stays open holding the outcome.
            ReloadCurrentClusterPage();
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// The manifest editor as a modal (KON-252), for the kinds whose page is a list of rows rather
    /// than a detail page with tabs.
    /// </summary>
    private void ShowManifestEditor(ResourceRef reference)
    {
        if (_cluster is null)
            return;

        Dialog = new EditManifestDialogViewModel(_cluster, reference, CloseDialog, onDone: () =>
        {
            // Keys and sizes live on the list behind it, and an apply changes them.
            ReloadCurrentClusterPage();
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Open one of the storage pages at a single object (KON-254).
    /// <para>
    /// The search box is the filter, rather than a private one the page owns: there is then one way
    /// to say "show me this one", it is visible, and it can be cleared. A page silently filtered to
    /// something the search box disagrees with is a page nobody can reason about.
    /// </para>
    /// </summary>
    private void OpenStorage(string key, string term)
    {
        NavigateCluster(key);

        if (CurrentPage is IListPage page)
        {
            page.SearchText = term;
            SearchText = term;
        }
    }

    private void ShowServicePortForward(Service service)
    {
        if (_cluster is null)
            return;

        var reference = new ResourceRef(GroupVersionKind.Service, service.Namespace, service.Name);
        var ports = service.Ports
            .Select(p => new PortChoice(p.Port, p.Name.Length > 0 ? $"{p.Port} · {p.Name}" : p.Port.ToString(CultureInfo.InvariantCulture)))
            .ToList();
        Dialog = new PortForwardViewModel(
            _portForwards, _cluster, reference, $"{service.Name} · {service.Namespace}", ports, CloseDialog,
            UpdatePortForwardCount);
    }

    /// <summary>
    /// A pod's ports come from the containers that declare them, so the label names the container as
    /// well: two containers publishing 8080 are otherwise two identical rows (KON-170). Before this the
    /// list was passed in empty and the dialog fell back to 80 for every pod in the cluster.
    /// </summary>
    private void ShowPodPortForward(Pod pod)
    {
        if (_cluster is null)
            return;

        var reference = new ResourceRef(GroupVersionKind.Pod, pod.Namespace, pod.Name);
        var containers = pod.AllContainers;
        var multiple = containers.Count(c => c.Ports.Count > 0) > 1;

        var ports = containers
            .SelectMany(c => c.Ports.Select(p => new PortChoice(
                p.Number,
                (p.Name.Length, multiple) switch
                {
                    ( > 0, true) => $"{p.Number} · {p.Name} ({c.Name})",
                    ( > 0, false) => $"{p.Number} · {p.Name}",
                    (_, true) => $"{p.Number} ({c.Name})",
                    _ => p.Number.ToString(CultureInfo.InvariantCulture),
                })))
            .ToList();

        Dialog = new PortForwardViewModel(
            _portForwards, _cluster, reference, $"{pod.Name} · {pod.Namespace}", ports, CloseDialog,
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

        // Refresh the nav counts, then reload the visible namespaced grid — in that order, because
        // the counts are what decides which Workloads page this namespace gets (KON-200).
        //
        // The page comes from _clusterPageKey rather than from whichever nav item carries IsSelected,
        // for the reason NavigateCluster records: the per-kind children are rebuilt as workloads come
        // and go, so on a per-kind page there may be no selected item at all — and this then quietly
        // navigated to Overview instead of reloading the page you were on.
        _ = NavigateClusterAfterCountsAsync(_clusterPageKey);
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
    /// <summary>
    /// Open the pod-detail page for a pod (logs / shell / events / YAML).
    /// <para>
    /// Back is the history's, not a fixed destination. This used to take an <c>onBack</c> that each
    /// caller filled in so a pod opened from a workload returned to that workload — the history
    /// replayed by hand, one route at a time (KON-173).
    /// </para>
    /// </summary>
    private void ShowPodDetail(Pod pod)
    {
        if (_cluster is null)
            return;

        ShowDetail(
            new ClusterPodDetailViewModel(
                _cluster, pod, CurrentTerminalFont(), ShowPodPortForward, _portForwards, OpenEventObjectAsync,
                onDelete: () => ConfirmDeletePod(pod), usageGraphs: CurrentUsageGraphs()),
            $"pod {pod.Name}", pod);
    }

    /// <summary>
    /// Open the workload-detail page (KON-166). Until this existed a workload row was a dead end:
    /// Scale and Restart, and no way from a Deployment to the pods it controls.
    /// </summary>
    private void ShowWorkloadDetail(Workload workload)
    {
        if (_cluster is null)
            return;

        ShowDetail(new ClusterWorkloadDetailViewModel(
            _cluster, workload,
            onOpenPod: ShowPodDetail,
            onScale: ShowScaleDialog,
            onRestart: ConfirmRestartWorkload,
            onDelete: () => ConfirmDeleteWorkload(workload)),
            $"{workload.Kind} {workload.Name}", workload);
    }

    /// <summary>
    /// Open a ConfigMap's or a Secret's detail (KON-330). The row's own object is handed over rather
    /// than a reference, because it already carries the key names, the sizes and the fetcher — asking
    /// the cluster again for what the list just read would be a second answer to the same question.
    /// </summary>
    private void ShowConfigDetail(ConfigObjectRow row)
    {
        if (_cluster is null || row is null)
            return;

        ShowDetail(
            new ClusterConfigDetailViewModel(
                _cluster, row, onOpenPod: ShowPodDetail, onDelete: () => ConfirmDeleteConfigObject(row)),
            $"{(row.IsSecret ? "secret" : "config map")} {row.Name}", row);
    }

    /// <summary>Open the service-detail page (KON-167).</summary>
    private void ShowServiceDetail(Service service)
    {
        if (_cluster is null)
            return;

        ShowDetail(new ClusterServiceDetailViewModel(
            _cluster, service,
            onOpenPod: ShowPodDetail,
            onForward: ShowServicePortForward,
            portForwards: _portForwards,
            onDelete: () => ConfirmDeleteService(service)),
            $"service {service.Name}", service);
    }

    /// <summary>
    /// Open whatever an event is about (KON-248) — the events feed's one way out.
    /// <para>
    /// An event carries a <see cref="ResourceRef"/>, and the detail pages take the object itself, so
    /// this looks it up in the namespace the event names. False means it is no longer there, which
    /// is ordinary rather than exceptional: events outlive their objects by design, and a crash-looping
    /// pod that has since been replaced is the single most likely row to be clicked.
    /// </para>
    /// </summary>
    private async Task<bool> OpenEventObjectAsync(ResourceRef target)
    {
        if (_cluster is null)
            return false;

        var ns = target.Namespace;

        switch (target.Kind.Kind)
        {
            case "Pod":
                if ((await _cluster.ListPodsAsync(ns)).FirstOrDefault(p => p.Name == target.Name) is not { } pod)
                    return false;

                ShowPodDetail(pod);
                return true;

            case "Service":
                if ((await _cluster.ListServicesAsync(ns)).FirstOrDefault(s => s.Name == target.Name) is not { } service)
                    return false;

                ShowServiceDetail(service);
                return true;

            case var kind when Enum.TryParse<WorkloadKind>(kind, out var workloadKind):
                if ((await _cluster.ListWorkloadsAsync(workloadKind, ns))
                        .FirstOrDefault(w => w.Name == target.Name) is not { } workload)
                    return false;

                ShowWorkloadDetail(workload);
                return true;

            default:
                // The row only offers the link for kinds that have a page, so this is the belt to that
                // braces: a kind added to one list and not the other lands here rather than nowhere.
                return false;
        }
    }

    /// <summary>
    /// What a host shell needs to start on the cluster being shown (KON-171): the context, the names
    /// its entry points at, and the kubeconfig files already in play.
    /// <para>
    /// The namespace comes from the picker rather than the context's own default — the terminal should
    /// open where the rest of the window is looking. With <em>All namespaces</em> selected there is
    /// nothing to pin, and the context's default stands.
    /// </para>
    /// <para>
    /// Returns null when the active backend is not a Kubernetes context, which is what keeps the nav
    /// item off the engine side.
    /// </para>
    /// </summary>
    /// <summary>
    /// The Terminal page, wired to the shell kept for this cluster (KON-171).
    /// <para>
    /// The header follows the shell that is actually running rather than the pickers: its context and
    /// namespace were fixed when it started, and changing the namespace afterwards does not reach into a
    /// shell that is already open. Reconnect discards it, and the next open reads the pickers again.
    /// </para>
    /// </summary>
    private ClusterTerminalsViewModel? CreateClusterTerminals()
    {
        if (BuildShellRequest() is null || _activeBackend is not { Length: > 0 } backend)
            return null;

        return new ClusterTerminalsViewModel(
            _terminals,
            backend,
            // Read afresh per terminal rather than captured: a new tab opens on the namespace selected
            // now, which is not necessarily the one the page was built with.
            request: () => BuildShellRequest()!,
            font: CurrentTerminalFont,
            onCountChanged: () => UpdateTerminalCount(backend));
    }

    /// <summary>Badge the sidebar with how many shells are open, the way port forwards does.</summary>
    private void UpdateTerminalCount(string backend)
    {
        var count = _terminals.CountFor(backend);
        SetNavCount("terminal", count > 1 ? count.ToString(CultureInfo.InvariantCulture) : string.Empty);
    }

    private ClusterShellRequest? BuildShellRequest()
    {
        if (_activeBackend is not { Length: > 0 } backend || !IsClusterMode)
            return null;

        var context = Pretty(backend);
        var kubeconfigPath = _probes
            .Select(p => p.Provider)
            .OfType<KubernetesClusterProvider>()
            .FirstOrDefault(p => p.Backend == backend)?
            .KubeconfigPath;

        // Cluster and user are needed to pin a namespace at all: an overlay naming neither would
        // shadow the real context instead of adding to it. They are names from the user's own file,
        // not credentials.
        var entry = Kubeconfig.LoadContexts(kubeconfigPath).FirstOrDefault(c => c.Name == context);

        var paths = new List<string>();
        if (kubeconfigPath is { Length: > 0 })
            paths.Add(Kubeconfig.Expand(kubeconfigPath));
        paths.Add(Kubeconfig.DefaultPath);
        paths.AddRange(_settings.KubeconfigPaths.Select(Kubeconfig.Expand));

        return new ClusterShellRequest(
            context,
            entry?.Cluster,
            entry?.User,
            ActiveNamespace ?? entry?.Namespace,
            paths);
    }
}
