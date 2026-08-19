using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Adapters.Kubernetes;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;
using Kontena.Core.Orchestration;

namespace Kontena.App.ViewModels;

// The cluster resource browsers behind the k8s nav (KON-73). Each reads its list from an
// IClusterEngine — the FakeClusterEngine today, a real adapter (KON-68) later — and filters by
// the command-bar namespace picker where namespaces apply. Pod/workload detail, actions, and the
// apply flow are their own tickets (KON-69/70/71).

/// <summary>Nodes view — a card per node with CPU/memory gauges (see k8s-nodes mockup).</summary>
public partial class ClusterNodesViewModel : ClusterListPageViewModel<NodeCardRow>
{
    private readonly IClusterEngine _cluster;

    private readonly Action<string>? _onDrain;
    private readonly Action<Node>? _onOpenDetail;

    /// <param name="onDrain">Opens the drain modal for a node; the shell owns the dialog because a
    /// drain outlives the page it was started from.</param>
    /// <param name="onOpenDetail">Opens the node-detail page (KON-197).</param>
    public ClusterNodesViewModel(
        IClusterEngine cluster, Action<string>? onDrain = null, Action<Node>? onOpenDetail = null)
        : base(cluster, GroupVersionKind.Node, null)
    {
        _cluster = cluster;
        _onDrain = onDrain;
        _onOpenDetail = onOpenDetail;

        // Plenty of clusters (kind, plain kubeadm) ship without a usage backend. Rather than
        // leaving four dashes unexplained, say so once and say what would fix it.
        ShowMetricsNotice = !cluster.Capabilities.Metrics;
        MetricsNoticeDetail = cluster is IMetricsAware { Metrics.Name: var source } && source != "none"
            ? $"Kontena looked for {source} and it did not answer."
            : "This cluster has no usage backend configured.";

        _ = LoadAsync();
        StartWatching();
    }

    public override string SearchPlaceholder => "Search nodes…";

    /// <summary>Whether to explain the missing CPU/memory gauges.</summary>
    [ObservableProperty] private bool _showMetricsNotice;

    /// <summary>Which source was tried, when the backend can tell us.</summary>
    public string MetricsNoticeDetail { get; }

    /// <summary>What the user can do about it — and, since KON-93, what Kontena can do for them.</summary>
    public string MetricsNoticeAction { get; } =
        "Kontena can install metrics-server for you. Node status, conditions and pod counts do not need" +
        " a metrics source and are unaffected.";

    /// <summary>
    /// Whether the install can be offered at all: it writes to the cluster, so a backend without the
    /// mutating half has nothing to offer (KON-86).
    /// </summary>
    public bool CanInstallMetrics => _cluster.Capabilities.Apply;

    /// <summary>Set while the manifest is being applied and the rollout waited for.</summary>
    [ObservableProperty] private bool _isInstallingMetrics;

    /// <summary>Where the install has got to, in the words of someone watching it.</summary>
    [ObservableProperty] private string _metricsInstallStatus = string.Empty;

    /// <summary>The context the cluster reported, which is what the insecure-kubelet guess reads.</summary>
    private string _context = string.Empty;

    protected override async Task<IReadOnlyList<NodeCardRow>> LoadRowsAsync(CancellationToken ct)
    {
        // The apiserver version is what a kubelet version means anything against (KON-95): a node is
        // only "behind" relative to its own control plane.
        var info = await _cluster.GetInfoAsync(ct);
        _context = info is ClusterInfo { Context: { Length: > 0 } context } ? context : string.Empty;

        return
        [
            .. (await _cluster.ListNodesAsync(ct: ct))
                .Select(n => new NodeCardRow(
                    n, info.Version,
                    canMaintain: _cluster.Capabilities.NodeMaintenance,
                    onCordon: ConfirmCordon,
                    onDrain: node => _onDrain?.Invoke(node.Name),
                    onOpenDetail: _onOpenDetail)),
        ];
    }

    // ── Node maintenance (KON-251) ───────────────────────────────────────────

    /// <summary>
    /// Cordon asks first, uncordon does not.
    /// <para>
    /// They are not opposites in what they risk: cordoning changes where new work can go and is the
    /// first half of taking a node out of service, while uncordoning only puts a node back to how
    /// every other node already is. A confirm on the harmless one is what teaches people to dismiss
    /// the other without reading.
    /// </para>
    /// </summary>
    private void ConfirmCordon(NodeCardRow node)
    {
        if (!node.Cordoned)
        {
            Confirm(
                "Cordon node?",
                $"Nothing new will be scheduled onto {node.Name}. The pods already running there stay"
                + " where they are — moving them off is a drain, and this is not one.",
                "Cordon",
                () => SetCordonAsync(node.Name, cordoned: true),
                destructive: false);
            return;
        }

        _ = SetCordonAsync(node.Name, cordoned: false);
    }

    private async Task SetCordonAsync(string node, bool cordoned)
    {
        try
        {
            Services.Diag.Action(cordoned ? "cordon node" : "uncordon node", node);
            await _cluster.CordonNodeAsync(node, cordoned);
            await LoadAsync();
        }
        catch (Exception failure)
        {
            // The same line the metrics install uses; a node action failing is nearly always RBAC.
            MetricsInstallStatus = $"Could not {(cordoned ? "cordon" : "uncordon")} {node} — {failure.Message}";
        }
    }

    // Roles and status as well as the name: "worker" and "NotReady" are how you actually go looking
    // through a node list.
    protected override bool Matches(NodeCardRow row, string term) =>
        Contains(row.Name, term) || Contains(row.Roles, term) || Contains(row.Status, term);

    // ── Installing a metrics source (KON-93) ─────────────────────────────────

    /// <summary>How long to wait for the rollout before saying so, and how often to look.</summary>
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ReadyPoll = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Offer the install, then do it. Confirmed first because it writes to the cluster — and the
    /// dialog names the release, the image and every kind it creates, read off the manifest itself
    /// rather than typed alongside it.
    /// </summary>
    [RelayCommand]
    private void InstallMetrics()
    {
        var insecure = MetricsServerInstall.LikelyNeedsInsecureKubeletTls(_context);

        var kubelet = insecure
            ? $"This cluster looks like kind or minikube, whose kubelet serves a self-signed"
              + " certificate, so it is installed with --kubelet-insecure-tls. Without that flag the"
              + " pod never becomes ready and the gauges stay empty."
            : "The kubelet's certificate is expected to be one metrics-server accepts. If the rollout"
              + " never becomes ready, that assumption is the first thing to check.";

        Confirm(
            "Install metrics-server?",
            $"Kontena applies the upstream metrics-server {MetricsServerInstall.Version} manifest to"
            + $" this cluster, in kube-system. {kubelet}",
            "Install",
            InstallMetricsAsync,
            destructive: false,
            details:
            [
                new ConfirmDetail("IconBox", MetricsServerInstall.Image, MetricsServerInstall.SourceUrl),
                new ConfirmDetail("IconLayers", "Creates", string.Join(", ", MetricsServerInstall.Creates())),
            ]);
    }

    private async Task InstallMetricsAsync()
    {
        IsInstallingMetrics = true;
        MetricsInstallStatus = "Applying the manifest…";

        try
        {
            var failures = new List<string>();
            var bundle = new ManifestBundle
            {
                Yaml = MetricsServerInstall.Manifest(
                    MetricsServerInstall.LikelyNeedsInsecureKubeletTls(_context)),
                Source = $"metrics-server {MetricsServerInstall.Version}",
                Namespace = "kube-system",
            };

            Services.Diag.Action("install metrics-server", MetricsServerInstall.Version);

            await foreach (var step in _cluster.ApplyAsync(bundle))
            {
                if (step.Action == ApplyAction.Failed)
                    failures.Add($"{step.Resource.Kind.Kind} {step.Resource.Name}: {step.Error}");
            }

            if (failures.Count > 0)
            {
                // Named, not counted: "3 resources failed" sends someone to the terminal to find out
                // which, and the RBAC one is the usual answer.
                MetricsInstallStatus = "Could not install it — " + string.Join("; ", failures);
                return;
            }

            MetricsInstallStatus = "Applied. Waiting for metrics-server to answer…";
            if (await WaitForMetricsAsync())
            {
                // The notice is the thing that was wrong; drop it and redraw the cards with gauges.
                ShowMetricsNotice = false;
                MetricsInstallStatus = string.Empty;
                await LoadAsync();
                return;
            }

            MetricsInstallStatus =
                $"Installed, but no usage arrived within {ReadyTimeout.TotalMinutes:0} minutes. It may still"
                + " be starting — reload this page, or check the metrics-server pod in kube-system.";
        }
        catch (Exception error)
        {
            MetricsInstallStatus = $"Could not install it — {error.Message}";
        }
        finally
        {
            IsInstallingMetrics = false;
        }
    }

    /// <summary>
    /// Poll until usage answers, or give up. A metrics-server takes a while to be scraped for the
    /// first time, so "applied" is not "working" and only the second is worth saying.
    /// </summary>
    private async Task<bool> WaitForMetricsAsync()
    {
        var deadline = DateTimeOffset.UtcNow + ReadyTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            // Ping is what re-probes the source and recomputes the capability on the real adapter, so
            // this asks the backend rather than deciding for it.
            try
            {
                await _cluster.PingAsync();
            }
            catch (Exception)
            {
                // A blip mid-rollout is not a failure; the deadline is what decides.
            }

            if (_cluster.Capabilities.Metrics)
                return true;

            await Task.Delay(ReadyPoll);
        }

        return false;
    }
}

/// <summary>Namespaces view.</summary>
public partial class ClusterNamespacesViewModel : ClusterListPageViewModel<NamespaceRow>
{
    private readonly IClusterEngine _cluster;

    private readonly Action<KubeNamespace>? _onOpenDetail;

    public ClusterNamespacesViewModel(IClusterEngine cluster, Action<KubeNamespace>? onOpenDetail = null)
        : base(cluster, GroupVersionKind.Namespace, null)
    {
        _cluster = cluster;
        _onOpenDetail = onOpenDetail;
        _ = LoadAsync();
        StartWatching();
    }

    public override string SearchPlaceholder => "Search namespaces…";

    protected override async Task<IReadOnlyList<NamespaceRow>> LoadRowsAsync(CancellationToken ct) =>
        [.. (await _cluster.ListNamespacesAsync(ct)).Select(ns => new NamespaceRow(ns, _onOpenDetail))];

    protected override bool Matches(NamespaceRow row, string term) => Contains(row.Name, term);

    protected override IReadOnlyDictionary<string, Func<NamespaceRow, IComparable>> SortColumns { get; } =
        new Dictionary<string, Func<NamespaceRow, IComparable>>(StringComparer.Ordinal)
        {
            ["NAME"] = r => r.Name,
            ["STATUS"] = r => r.Status,
            ["AGE"] = r => r.AgeSpan,
        };
}

/// <summary>Workloads view — the controllers (Deployment/StatefulSet/DaemonSet/Job/CronJob).</summary>
public partial class ClusterWorkloadsViewModel : ClusterListPageViewModel<WorkloadRow>
{
    private readonly IClusterEngine _cluster;
    private readonly string? _namespace;
    private readonly Action<Workload>? _onScale;
    private readonly Action<Workload>? _onRestart;
    private readonly Action<Workload>? _onOpenDetail;

    /// <param name="onOpenDetail">Invoked when a workload row is opened; the shell wires this to the
    /// workload-detail page (KON-166). A constructor parameter rather than an init-property, so it is
    /// set before the fire-and-forget load builds the rows.</param>
    /// <param name="kind">One kind, or null for every kind in one list (KON-169).</param>
    public ClusterWorkloadsViewModel(
        IClusterEngine cluster, string? @namespace,
        Action<Workload>? onScale = null, Action<Workload>? onRestart = null,
        Action<Workload>? onOpenDetail = null, WorkloadKind? kind = null)
        // One kind has a coordinate to follow; the all-kinds page is five kinds at once, and a watch
        // per kind is five streams whose bursts would land out of step with each other.
        : base(
            cluster,
            kind is { } k ? GroupVersionKind.For(k) : null,
            @namespace,
            unwatchable: "This page shows several kinds at once and updates when you refresh it."
                + " A single kind's page follows the cluster on its own.")
    {
        _cluster = cluster;
        _namespace = @namespace;
        _onScale = onScale;
        _onRestart = onRestart;
        _onOpenDetail = onOpenDetail;
        _kind = kind;
        _ = LoadAsync();
        StartWatching();
    }

    private readonly WorkloadKind? _kind;

    public override string SearchPlaceholder => _kind is { } k ? $"Search {k.ToString().ToLowerInvariant()}s…" : "Search workloads…";

    /// <summary>"Workloads", or "Deployments" when the page shows a single kind.</summary>
    public string Title => _kind is { } k ? k + "s" : "Workloads";

    /// <summary>
    /// Whether to show the KIND column. On a single-kind page it repeats the heading on every row,
    /// which is the space the kind-specific columns below want.
    /// </summary>
    public bool ShowKindColumn => _kind is null;

    /// <summary>
    /// A CronJob's schedule, in place of replica counts it does not have. This is the actual reason a
    /// single list wrings: the shared columns are the lowest common denominator of every kind, so the
    /// one field you opened the page for is the one that has nowhere to go.
    /// </summary>
    public bool ShowScheduleColumn => _kind == WorkloadKind.CronJob;

    /// <summary>A DaemonSet has no replicas — its READY is per node, so the column is labelled for it.</summary>
    public string ReadyHeader => _kind switch
    {
        WorkloadKind.DaemonSet => "READY / NODES",
        WorkloadKind.CronJob => "ACTIVE",
        WorkloadKind.Job => "COMPLETIONS",
        _ => "READY",
    };

    /// <summary>
    /// Shown when a kind's page is empty, so it does not look like a failed load. It names the
    /// namespace, because a kind keeps its sidebar entry in a namespace that runs none of it
    /// (KON-414): this line is the only place the user is told that it is the namespace that is empty
    /// and not the cluster.
    /// </summary>
    public string EmptyText =>
        $"No {_kind?.ToString() ?? "workload"} objects found for "
        + (_namespace is { } ns ? $"namespace {ns}." : "any namespace.");

    /// <summary>Delete a workload, always confirmed (KON-332).</summary>
    private void ConfirmDelete(WorkloadRow row)
    {
        var (title, message) = ClusterDeleteWording.Workload(row.Kind, row.Name, row.Namespace);

        ConfirmDelete(title, message, async () =>
        {
            await _cluster.DeleteAsync(row.Reference);
            await LoadAsync();
        });
    }

    protected override async Task<IReadOnlyList<WorkloadRow>> LoadRowsAsync(CancellationToken ct) =>
        [.. (await _cluster.ListWorkloadsAsync(_kind, _namespace, ct))
            .Select(w => new WorkloadRow(w, _onScale, _onRestart, _onOpenDetail, ConfirmDelete))];

    protected override bool Matches(WorkloadRow row, string term) =>
        Contains(row.Name, term) || Contains(row.Kind, term) || Contains(row.Namespace, term);

    protected override IReadOnlyDictionary<string, Func<WorkloadRow, IComparable>> SortColumns { get; } =
        new Dictionary<string, Func<WorkloadRow, IComparable>>(StringComparer.Ordinal)
        {
            ["NAME"] = r => r.Name,
            ["NAMESPACE"] = r => r.Namespace,
            ["KIND"] = r => r.Kind,
            ["STATUS"] = r => r.Status,
            ["AGE"] = r => r.AgeSpan,
        };
}

/// <summary>Pods view.</summary>
public partial class ClusterPodsViewModel : ClusterListPageViewModel<PodRow>
{
    private readonly IClusterEngine _cluster;
    private readonly string? _namespace;
    private readonly Action<Pod>? _onOpenDetail;
    private readonly Action<Pod>? _onDelete;

    /// <param name="onOpenDetail">Invoked when a pod row is opened; the shell wires this to the
    /// pod-detail page. Passed via the constructor (not an init-property) so it is set before the
    /// fire-and-forget load builds the rows.</param>
    /// <param name="onDelete">Invoked for a row's Delete action; the shell confirms first.</param>
    public ClusterPodsViewModel(
        IClusterEngine cluster, string? @namespace, Action<Pod>? onOpenDetail = null, Action<Pod>? onDelete = null)
        : base(cluster, GroupVersionKind.Pod, @namespace)
    {
        _cluster = cluster;
        _namespace = @namespace;
        _onOpenDetail = onOpenDetail;
        _onDelete = onDelete;
        _ = LoadAsync();
        StartWatching();
    }

    public override string SearchPlaceholder => "Search pods…";

    /// <summary>The phase filter dropdown's options — "All" plus every value a pod's phase actually
    /// takes (KON-320). Not an enum-wide list: Unknown exists in the type and essentially never on a
    /// real pod, and an option nothing ever matches is worse than no option.</summary>
    public IReadOnlyList<string> PhaseFilterOptions { get; } =
        ["All", "Running", "Pending", "Succeeded", "Failed"];

    /// <summary>
    /// Same idea as the events page's "warnings only" (KON-248): a filter the page owns, distinct
    /// from the search box, applied whether or not anything has been typed.
    /// </summary>
    [ObservableProperty] private string _phaseFilter = "All";

    partial void OnPhaseFilterChanged(string value) => ApplyFilter();

    protected override bool Include(PodRow row) => PhaseFilter == "All" || row.PhaseRaw.ToString() == PhaseFilter;

    protected override async Task<IReadOnlyList<PodRow>> LoadRowsAsync(CancellationToken ct) =>
        [.. (await _cluster.ListPodsAsync(_namespace, ct)).Select(p => new PodRow(p, _onOpenDetail, _onDelete))];

    // Node and status too: "which pods are on worker-2" and "what is CrashLooping" are the two
    // questions a pod list gets asked.
    protected override bool Matches(PodRow row, string term) =>
        Contains(row.Name, term) || Contains(row.Namespace, term)
        || Contains(row.Node, term) || Contains(row.Phase, term);

    protected override IReadOnlyDictionary<string, Func<PodRow, IComparable>> SortColumns { get; } =
        new Dictionary<string, Func<PodRow, IComparable>>(StringComparer.Ordinal)
        {
            ["NAME"] = r => r.Name,
            ["NAMESPACE"] = r => r.Namespace,
            ["READY"] = r => r.ReadyRaw,
            ["STATUS"] = r => r.Phase,
            ["RESTARTS"] = r => r.RestartsRaw,
            ["NODE"] = r => r.Node,
            ["AGE"] = r => r.AgeSpan,
        };
}

/// <summary>Services view.</summary>
public partial class ClusterServicesViewModel : ClusterListPageViewModel<ServiceRow>
{
    private readonly IClusterEngine _cluster;
    private readonly string? _namespace;

    private readonly Action<Service>? _onForward;
    private readonly Action<Service>? _onOpenDetail;

    public ClusterServicesViewModel(
        IClusterEngine cluster, string? @namespace,
        Action<Service>? onForward = null, Action<Service>? onOpenDetail = null)
        : base(cluster, GroupVersionKind.Service, @namespace)
    {
        _cluster = cluster;
        _namespace = @namespace;
        _onForward = onForward;
        _onOpenDetail = onOpenDetail;
        _ = LoadAsync();
        StartWatching();
    }

    public override string SearchPlaceholder => "Search services…";

    /// <summary>Delete a service, always confirmed (KON-332).</summary>
    private void ConfirmDelete(ServiceRow row)
    {
        var (title, message) = ClusterDeleteWording.Service(row.Name, row.Namespace, row.IsLoadBalancer);

        ConfirmDelete(title, message, async () =>
        {
            await _cluster.DeleteAsync(row.Reference);
            await LoadAsync();
        });
    }

    protected override async Task<IReadOnlyList<ServiceRow>> LoadRowsAsync(CancellationToken ct) =>
        [.. (await _cluster.ListServicesAsync(_namespace, ct))
            .Select(s => new ServiceRow(s, _onForward, _onOpenDetail, ConfirmDelete))];

    protected override bool Matches(ServiceRow row, string term) =>
        Contains(row.Name, term) || Contains(row.Namespace, term)
        || Contains(row.Type, term) || Contains(row.Ports, term);

    protected override IReadOnlyDictionary<string, Func<ServiceRow, IComparable>> SortColumns { get; } =
        new Dictionary<string, Func<ServiceRow, IComparable>>(StringComparer.Ordinal)
        {
            ["NAME"] = r => r.Name,
            ["NAMESPACE"] = r => r.Namespace,
            ["TYPE"] = r => r.Type,
            ["CLUSTER IP"] = r => r.ClusterIp,
            ["AGE"] = r => r.AgeSpan,
        };
}

/// <summary>Ingresses view — what is reachable from outside, and through which class (KON-247).</summary>
public partial class ClusterIngressesViewModel : ClusterListPageViewModel<IngressRow>
{
    private readonly IClusterEngine _cluster;
    private readonly string? _namespace;

    public ClusterIngressesViewModel(IClusterEngine cluster, string? @namespace)
        : base(cluster, GroupVersionKind.Ingress, @namespace)
    {
        _cluster = cluster;
        _namespace = @namespace;
        _ = LoadAsync();
        StartWatching();
    }

    public override string SearchPlaceholder => "Search ingresses…";

    /// <summary>Delete an ingress, always confirmed (KON-332).</summary>
    private void ConfirmDelete(IngressRow row)
    {
        var (title, message) = ClusterDeleteWording.Ingress(row.Name, row.Namespace);

        ConfirmDelete(title, message, async () =>
        {
            await _cluster.DeleteAsync(row.Reference);
            await LoadAsync();
        });
    }

    protected override async Task<IReadOnlyList<IngressRow>> LoadRowsAsync(CancellationToken ct) =>
        [.. (await _cluster.ListIngressesAsync(_namespace, ct)).Select(i => new IngressRow(i, ConfirmDelete))];

    // The host is the thing you know: someone reports that app.example.com is down and the ingress is
    // what you go looking for. The class matters when a cluster runs more than one controller.
    protected override bool Matches(IngressRow row, string term) =>
        Contains(row.Name, term) || Contains(row.Namespace, term)
        || Contains(row.Class, term) || Contains(row.Hosts, term);

    protected override IReadOnlyDictionary<string, Func<IngressRow, IComparable>> SortColumns { get; } =
        new Dictionary<string, Func<IngressRow, IComparable>>(StringComparer.Ordinal)
        {
            ["NAME"] = r => r.Name,
            ["NAMESPACE"] = r => r.Namespace,
            ["CLASS"] = r => r.Class,
            ["HOSTS"] = r => r.Hosts,
            ["ADDRESS"] = r => r.Address,
            ["AGE"] = r => r.AgeSpan,
        };
}

/// <summary>PersistentVolumeClaims view — what asked for storage, and whether it got any (KON-247).</summary>
public partial class ClusterPvcsViewModel : ClusterListPageViewModel<PvcRow>
{
    private readonly IClusterEngine _cluster;
    private readonly string? _namespace;

    private readonly Action<string>? _onOpenVolume;
    private readonly Action<string>? _onOpenClass;

    /// <param name="onOpenVolume">Route to the volume a bound claim sits on (KON-254).</param>
    /// <param name="onOpenClass">Route to the class that provisions it — where a Pending claim's
    /// reason lives.</param>
    public ClusterPvcsViewModel(
        IClusterEngine cluster, string? @namespace,
        Action<string>? onOpenVolume = null, Action<string>? onOpenClass = null)
        : base(cluster, GroupVersionKind.PersistentVolumeClaim, @namespace)
    {
        _cluster = cluster;
        _namespace = @namespace;
        _onOpenVolume = onOpenVolume;
        _onOpenClass = onOpenClass;
        _ = LoadAsync();
        StartWatching();
    }

    public override string SearchPlaceholder => "Search volume claims…";

    protected override async Task<IReadOnlyList<PvcRow>> LoadRowsAsync(CancellationToken ct) =>
        [.. (await _cluster.ListPvcsAsync(_namespace, ct)).Select(p => new PvcRow(p, _onOpenVolume, _onOpenClass))];

    // Status and storage class as well: "what is still Pending" and "what is on the slow class" are
    // the two questions a claim list gets asked.
    protected override bool Matches(PvcRow row, string term) =>
        Contains(row.Name, term) || Contains(row.Namespace, term)
        || Contains(row.Status, term) || Contains(row.StorageClass, term);

    protected override IReadOnlyDictionary<string, Func<PvcRow, IComparable>> SortColumns { get; } =
        new Dictionary<string, Func<PvcRow, IComparable>>(StringComparer.Ordinal)
        {
            ["NAME"] = r => r.Name,
            ["NAMESPACE"] = r => r.Namespace,
            ["STATUS"] = r => r.Status,
            ["VOLUME"] = r => r.Volume,
            ["CAPACITY"] = r => r.CapacityBytes,
            ["STORAGECLASS"] = r => r.StorageClass,
            ["AGE"] = r => r.AgeSpan,
        };
}


/// <summary>PersistentVolumes — the other half of a claim (KON-254). Cluster-scoped.</summary>
public partial class ClusterVolumesViewModel : ClusterListPageViewModel<PersistentVolumeRow>
{
    private readonly IClusterEngine _cluster;
    private readonly Action<string>? _onOpenClaim;
    private readonly Action<string>? _onOpenClass;

    public ClusterVolumesViewModel(
        IClusterEngine cluster, Action<string>? onOpenClaim = null, Action<string>? onOpenClass = null)
        : base(cluster, GroupVersionKind.PersistentVolume, null)
    {
        _cluster = cluster;
        _onOpenClaim = onOpenClaim;
        _onOpenClass = onOpenClass;
        _ = LoadAsync();
        StartWatching();
    }

    public override string SearchPlaceholder => "Search volumes…";

    protected override async Task<IReadOnlyList<PersistentVolumeRow>> LoadRowsAsync(CancellationToken ct) =>
        [.. (await _cluster.ListVolumesAsync(ct)).Select(v => new PersistentVolumeRow(v, _onOpenClaim, _onOpenClass))];

    // The claim as well: you arrive here from a claim far more often than you arrive at a volume by
    // its generated name, which nobody has ever typed on purpose.
    protected override bool Matches(PersistentVolumeRow row, string term) =>
        Contains(row.Name, term) || Contains(row.Claim, term)
        || Contains(row.Status, term) || Contains(row.StorageClass, term);

    protected override IReadOnlyDictionary<string, Func<PersistentVolumeRow, IComparable>> SortColumns { get; } =
        new Dictionary<string, Func<PersistentVolumeRow, IComparable>>(StringComparer.Ordinal)
        {
            ["NAME"] = r => r.Name,
            ["STATUS"] = r => r.Status,
            ["CAPACITY"] = r => r.CapacityBytes,
            ["CLAIM"] = r => r.Claim,
            ["STORAGECLASS"] = r => r.StorageClass,
            ["RECLAIM"] = r => r.Reclaim,
            ["DRIVER"] = r => r.Driver,
            ["AGE"] = r => r.AgeSpan,
        };
}

/// <summary>StorageClasses — where a Pending claim's reason lives (KON-254).</summary>
public partial class ClusterStorageClassesViewModel : ClusterListPageViewModel<StorageClassRow>
{
    private readonly IClusterEngine _cluster;

    public ClusterStorageClassesViewModel(IClusterEngine cluster)
        : base(cluster, GroupVersionKind.StorageClass, null)
    {
        _cluster = cluster;
        _ = LoadAsync();
        StartWatching();
    }

    public override string SearchPlaceholder => "Search storage classes…";

    protected override async Task<IReadOnlyList<StorageClassRow>> LoadRowsAsync(CancellationToken ct) =>
        [.. (await _cluster.ListStorageClassesAsync(ct)).Select(c => new StorageClassRow(c))];

    protected override bool Matches(StorageClassRow row, string term) =>
        Contains(row.Name, term) || Contains(row.Provisioner, term);

    protected override IReadOnlyDictionary<string, Func<StorageClassRow, IComparable>> SortColumns { get; } =
        new Dictionary<string, Func<StorageClassRow, IComparable>>(StringComparer.Ordinal)
        {
            ["NAME"] = r => r.Name,
            ["PROVISIONER"] = r => r.Provisioner,
            ["RECLAIM"] = r => r.Reclaim,
            ["AGE"] = r => r.AgeSpan,
        };
}

// ── Row view-models ─────────────────────────────────────────────────────────

public sealed partial class NodeCardRow
{
    private readonly Node _node;
    private readonly Action<NodeCardRow>? _onCordon;
    private readonly Action<NodeCardRow>? _onDrain;
    private readonly Action<Node>? _onOpenDetail;

    public NodeCardRow(
        Node n, string? apiServerVersion = null, bool canMaintain = false,
        Action<NodeCardRow>? onCordon = null, Action<NodeCardRow>? onDrain = null,
        Action<Node>? onOpenDetail = null)
    {
        ArgumentNullException.ThrowIfNull(n);

        _node = n;
        _onCordon = onCordon;
        _onDrain = onDrain;
        _onOpenDetail = onOpenDetail;
        CanMaintain = canMaintain && onCordon is not null;
        CanOpen = onOpenDetail is not null;

        Name = n.Name;
        Roles = n.Roles.Count > 0 ? string.Join(", ", n.Roles) : "—";
        Status = n.Status;
        Version = n.KubeletVersion;
        Cordoned = n.Unschedulable;

        var cap = n.Capacity;
        var use = n.Usage;
        CpuFraction = use is not null && cap.CpuMillicores > 0 ? (double)use.CpuMillicores / cap.CpuMillicores : 0;
        MemoryFraction = use is not null && cap.MemoryBytes > 0 ? (double)use.MemoryBytes / cap.MemoryBytes : 0;
        CpuText = use is null ? "—" : $"{use.CpuMillicores}m / {cap.CpuMillicores}m";
        MemoryText = use is null ? "—" : $"{Format.Size(use.MemoryBytes)} / {Format.Size(cap.MemoryBytes)}";

        // Disk only appears when the active source reports it — a metrics-server never does, so an
        // always-visible gauge would sit empty on half the clusters out there.
        HasDisk = use?.DiskUsedBytes is not null && cap.DiskBytes > 0;
        DiskFraction = HasDisk ? (double)use!.DiskUsedBytes!.Value / cap.DiskBytes : 0;
        DiskText = HasDisk ? $"{Format.Size(use!.DiskUsedBytes!.Value)} / {Format.Size(cap.DiskBytes)}" : "—";

        // Pod counts come off the pod list, so they show even when there is no metrics source.
        PodsText = $"{n.ScheduledPods} / {cap.Pods}";

        // Conditions need no metrics source either. Only the failing ones are worth surfacing —
        // a healthy node's five green conditions are noise, and the Ready dot already says it.
        Problems = [.. n.Problems.Select(c => new NodeProblemChip(c))];

        // Version skew is the same kind of signal: no metrics source, no network, just the two
        // numbers we already hold (KON-95). Shown only when it is outside the supported window.
        Skew = VersionSkewPolicy.Evaluate(apiServerVersion, n.KubeletVersion);
    }

    public string Name { get; }
    public string Roles { get; }
    public string Status { get; }
    public string Version { get; }
    public bool Cordoned { get; }
    public double CpuFraction { get; }
    public double MemoryFraction { get; }
    public string CpuText { get; }
    public string MemoryText { get; }
    public string PodsText { get; }

    /// <summary>Whether the active metrics source reported disk for this node.</summary>
    public bool HasDisk { get; }

    public double DiskFraction { get; }
    public string DiskText { get; }

    /// <summary>Conditions currently signalling trouble; empty on a healthy node.</summary>
    public IReadOnlyList<NodeProblemChip> Problems { get; }

    public bool HasProblems => Problems.Count > 0;

    /// <summary>How this node's kubelet sits against the apiserver.</summary>
    public NodeVersionSkew Skew { get; }

    /// <summary>Only an unsupported skew is worth a chip — a matching kubelet says nothing new.</summary>
    public bool HasVersionWarning => Skew.IsProblem;

    public string VersionWarning => Skew.Summary;
    public string VersionWarningDetail => Skew.Detail;

    /// <summary>A kubelet ahead of the control plane is an error; trailing too far is a warning.</summary>
    public IBrush VersionWarningBrush =>
        new SolidColorBrush(Color.Parse(Skew.State == VersionSkewState.Ahead ? "#F87171" : "#F5B14C"));

    public IBrush VersionWarningBackground =>
        new SolidColorBrush(Color.Parse(Skew.State == VersionSkewState.Ahead ? "#F87171" : "#F5B14C"), 0.13);

    public IBrush StatusBrush => new SolidColorBrush(Color.Parse(Status == "Ready" ? "#34D399" : "#F87171"));

    // ── Maintenance (KON-251) ───────────────────────────────────────────────

    /// <summary>Whether this backend can cordon and drain at all; false hides both buttons.</summary>
    public bool CanMaintain { get; }

    /// <summary>"Uncordon" on a node that is already cordoned — one button, both directions.</summary>
    public string CordonLabel => Cordoned ? "Uncordon" : "Cordon";

    /// <summary>
    /// Draining a node that is not cordoned is the ordinary case; draining one that is cordoned is
    /// how you finish a job you started. Neither is hidden.
    /// </summary>
    [RelayCommand]
    private void Cordon() => _onCordon?.Invoke(this);

    [RelayCommand]
    private void Drain() => _onDrain?.Invoke(this);

    /// <summary>Whether the shell wired a detail page to arrive at (KON-197).</summary>
    public bool CanOpen { get; }

    [RelayCommand]
    private void Open() => _onOpenDetail?.Invoke(_node);
}

/// <summary>
/// A failing node condition, as a chip on the node card. Pressure conditions are a warning — the
/// node still runs, but the kubelet may start evicting — while a failing Ready is a hard problem.
/// </summary>
public sealed class NodeProblemChip
{
    public NodeProblemChip(NodeCondition condition)
    {
        Label = condition.Type;
        Detail = string.IsNullOrEmpty(condition.Message) ? condition.Reason : condition.Message;

        var colour = condition.Type == "Ready" ? "#F87171" : "#F5B14C";
        Brush = new SolidColorBrush(Color.Parse(colour));
        Background = new SolidColorBrush(Color.Parse(colour), 0.13);
    }

    public string Label { get; }
    public string Detail { get; }
    public IBrush Brush { get; }
    public IBrush Background { get; }
}

public sealed partial class NamespaceRow
{
    private readonly KubeNamespace _namespace;
    private readonly Action<KubeNamespace>? _onOpenDetail;

    public NamespaceRow(KubeNamespace ns, Action<KubeNamespace>? onOpenDetail = null)
    {
        ArgumentNullException.ThrowIfNull(ns);

        _namespace = ns;
        _onOpenDetail = onOpenDetail;
        CanOpen = onOpenDetail is not null;

        Name = ns.Name;
        Status = ns.Phase;
        Age = Format.Duration(ns.Age);
        AgeSpan = ns.Age;
    }

    public string Name { get; }
    public string Status { get; }
    public string Age { get; }

    /// <summary>The raw age behind <see cref="Age"/> — what a column sort actually orders by (KON-318).</summary>
    public TimeSpan AgeSpan { get; }

    public bool CanOpen { get; }

    [RelayCommand]
    private void Open() => _onOpenDetail?.Invoke(_namespace);
}

public sealed partial class PersistentVolumeRow
{
    private readonly string _claimName;
    private readonly Action<string>? _onOpenClaim;
    private readonly Action<string>? _onOpenClass;

    public PersistentVolumeRow(PersistentVolume v, Action<string>? onOpenClaim = null, Action<string>? onOpenClass = null)
    {
        ArgumentNullException.ThrowIfNull(v);

        _onOpenClaim = onOpenClaim;
        _onOpenClass = onOpenClass;

        Name = v.Name;
        Status = v.Phase.ToString();
        Capacity = Format.Quantity(v.CapacityBytes);
        CapacityBytes = v.CapacityBytes;
        AccessModes = v.AccessModes.Count == 0 ? "—" : string.Join(", ", v.AccessModes);
        Reclaim = v.ReclaimPolicy.ToString();
        StorageClass = string.IsNullOrEmpty(v.StorageClass) ? "—" : v.StorageClass;
        Claim = string.IsNullOrEmpty(v.Claim) ? "—" : v.Claim;
        Driver = string.IsNullOrEmpty(v.Driver) ? "—" : v.Driver;
        Age = Format.Duration(v.Age);
        AgeSpan = v.Age;

        // The claim column is a route back, and only where there is a claim to route to.
        _claimName = v.Claim.Contains('/', StringComparison.Ordinal)
            ? v.Claim[(v.Claim.IndexOf('/', StringComparison.Ordinal) + 1)..]
            : v.Claim;
        CanOpenClaim = onOpenClaim is not null && _claimName.Length > 0;
        CanOpenClass = onOpenClass is not null && v.StorageClass.Length > 0;

        StatusBrush = new SolidColorBrush(Color.Parse(v.Phase switch
        {
            VolumePhase.Bound => "#34D399",
            VolumePhase.Available => "#5B9BD5",
            VolumePhase.Released => "#F5B14C",
            VolumePhase.Failed => "#F87171",
            _ => "#5C6675",
        }));

        // Released with Retain is the state that costs money quietly: the claim is gone, the data is
        // not, and nothing will reuse this volume until a person deals with it. Every other phase
        // either resolves itself or is already being looked at.
        Note = v.Phase == VolumePhase.Released && v.ReclaimPolicy == ReclaimPolicy.Retain
            ? "Its claim is gone and the data was kept. Nothing will bind to this volume again until"
              + " you delete it or clear its claim reference — until then it is storage you are still paying for."
            : v.Phase == VolumePhase.Released
                ? "Its claim is gone. With this reclaim policy the volume is on its way out."
                : null;
    }

    public string Name { get; }
    public string Status { get; }
    public string Capacity { get; }

    /// <summary>What a column sort orders CAPACITY/AGE by — the formatted text is for reading, not
    /// for comparing (KON-318).</summary>
    public long CapacityBytes { get; }

    public string AccessModes { get; }
    public string Reclaim { get; }
    public string StorageClass { get; }
    public string Claim { get; }
    public string Driver { get; }
    public string Age { get; }
    public TimeSpan AgeSpan { get; }
    public IBrush StatusBrush { get; }
    public bool CanOpenClaim { get; }
    public bool CanOpenClass { get; }

    /// <summary>What this phase means, where it means something worth acting on.</summary>
    public string? Note { get; }

    public bool HasNote => Note is not null;

    [RelayCommand]
    private void OpenClaim() => _onOpenClaim?.Invoke(_claimName);

    [RelayCommand]
    private void OpenClass() => _onOpenClass?.Invoke(StorageClass);
}

public sealed class StorageClassRow
{
    public StorageClassRow(StorageClass c)
    {
        ArgumentNullException.ThrowIfNull(c);

        Name = c.Name;
        Provisioner = string.IsNullOrEmpty(c.Provisioner) ? "—" : c.Provisioner;
        Reclaim = c.ReclaimPolicy.ToString();
        IsDefault = c.IsDefault;
        Expansion = c.AllowsExpansion ? "Yes" : "No";
        Age = Format.Duration(c.Age);

        // Said as a sentence rather than as the API's word. "WaitForFirstConsumer" is the single most
        // common reason someone thinks their storage is broken when it is working exactly as designed,
        // and the answer only helps if it is in language that reaches that conclusion.
        Binding = c.BindingMode == VolumeBindingMode.WaitForFirstConsumer
            ? "When a pod needs it"
            : "As soon as a claim exists";

        BindingDetail = c.BindingMode == VolumeBindingMode.WaitForFirstConsumer
            ? "A claim on this class stays Pending until a pod actually mounts it. That is not a fault."
            : "A claim on this class is provisioned straight away.";

        // A class with no provisioner never provisions anything. It is a legitimate configuration —
        // it means volumes are made by hand — and it is also what a typo produces.
        NoProvisioner = string.IsNullOrEmpty(c.Provisioner) || c.Provisioner == "kubernetes.io/no-provisioner";
        AgeSpan = c.Age;
    }

    public string Name { get; }
    public string Provisioner { get; }
    public string Reclaim { get; }
    public bool IsDefault { get; }
    public string Expansion { get; }
    public string Binding { get; }
    public string BindingDetail { get; }
    public bool NoProvisioner { get; }
    public string Age { get; }

    /// <summary>The raw age behind <see cref="Age"/> — what a column sort actually orders by (KON-318).</summary>
    public TimeSpan AgeSpan { get; }

    public string NoProvisionerDetail { get; } =
        "Nothing provisions volumes for this class, so a claim naming it waits for a volume someone"
        + " creates by hand.";
}


public sealed partial class IngressRow
{
    private readonly Action<IngressRow>? _onDelete;

    public IngressRow(Ingress i, Action<IngressRow>? onDelete = null)
    {
        ArgumentNullException.ThrowIfNull(i);

        _onDelete = onDelete;
        CanDelete = onDelete is not null;
        Reference = new ResourceRef(GroupVersionKind.Ingress, i.Namespace, i.Name);

        Name = i.Name;
        Namespace = i.Namespace;
        Class = string.IsNullOrEmpty(i.Class) ? "—" : i.Class;

        // A host repeats once per path, and the column is about which names reach this ingress at
        // all — so the cell is the distinct hosts and the tooltip is every rule in full.
        var hosts = i.Rules
            .Select(r => string.IsNullOrEmpty(r.Host) ? "*" : r.Host)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Hosts = hosts.Count == 0 ? "—" : string.Join("  ", hosts);

        // Same shape as the Services PORTS cell (KON-199): trimmed in the cell, complete on hover.
        var rules = i.Rules
            .Select(r =>
            {
                var host = string.IsNullOrEmpty(r.Host) ? "*" : r.Host;
                var path = string.IsNullOrEmpty(r.Path) ? "/" : r.Path;
                return $"{host}{path} → {r.ServiceName}:{r.ServicePort}";
            })
            .ToList();
        HostsTooltip = rules.Count == 0 ? null : string.Join("\n", rules);

        Address = i.Addresses.Count == 0 ? "—" : string.Join("  ", i.Addresses);
        AddressTooltip = i.Addresses.Count > 1 ? string.Join("\n", i.Addresses) : null;

        // Which hosts TLS covers, not merely that some certificate exists: an ingress with three hosts
        // and one of them in its TLS block is the case worth seeing, and "TLS ✓" would hide it.
        HasTls = i.TlsHosts.Count > 0;
        TlsTooltip = HasTls ? "TLS: " + string.Join(", ", i.TlsHosts) : null;

        // An ingress that routes nothing is a real and common mistake — a rules block that never
        // matched, or a service name with a typo in it. Nothing else on the row says so.
        HasNoRules = i.Rules.Count == 0;

        Age = Format.Duration(i.Age);
        AgeSpan = i.Age;
    }

    public string Name { get; }
    public string Namespace { get; }
    public string Class { get; }
    public string Hosts { get; }
    public string? HostsTooltip { get; }
    public string Address { get; }
    public string? AddressTooltip { get; }

    /// <summary>Whether any host is covered by TLS — the chip next to the hosts.</summary>
    public bool HasTls { get; }

    public string? TlsTooltip { get; }

    /// <summary>An ingress with no routing rules at all; worth flagging rather than showing a dash.</summary>
    public bool HasNoRules { get; }

    public string Age { get; }

    /// <summary>The raw age behind <see cref="Age"/> — what a column sort actually orders by (KON-318).</summary>
    public TimeSpan AgeSpan { get; }

    /// <summary>What the delete addresses (KON-332).</summary>
    public ResourceRef Reference { get; }

    /// <summary>Whether the page wired a delete handler (KON-332).</summary>
    public bool CanDelete { get; }

    [RelayCommand]
    private void Delete() => _onDelete?.Invoke(this);
}

public sealed partial class PvcRow
{
    private readonly string _volumeName;
    private readonly string _className;
    private readonly Action<string>? _onOpenVolume;
    private readonly Action<string>? _onOpenClass;

    public PvcRow(
        PersistentVolumeClaim p, Action<string>? onOpenVolume = null, Action<string>? onOpenClass = null)
    {
        ArgumentNullException.ThrowIfNull(p);

        _volumeName = p.Volume;
        _className = p.StorageClass;
        _onOpenVolume = onOpenVolume;
        _onOpenClass = onOpenClass;

        // Routes only where there is something at the other end (KON-254). An unbound claim has no
        // volume to go to, and a link that opens an empty list is worse than plain text.
        CanOpenVolume = onOpenVolume is not null && p.Volume.Length > 0;
        CanOpenClass = onOpenClass is not null && p.StorageClass.Length > 0;

        Name = p.Name;
        Namespace = p.Namespace;
        Status = p.Phase.ToString();
        Volume = string.IsNullOrEmpty(p.Volume) ? "—" : p.Volume;
        // Binary units, not Format.Size: this column sits next to someone's kubectl output.
        Capacity = Format.Quantity(p.CapacityBytes);
        CapacityBytes = p.CapacityBytes;
        StorageClass = string.IsNullOrEmpty(p.StorageClass) ? "—" : p.StorageClass;
        AccessModes = p.AccessModes.Count == 0 ? "—" : string.Join(", ", p.AccessModes);
        Age = Format.Duration(p.Age);
        AgeSpan = p.Age;

        // Same status palette as pods and workloads, so a colour means the same thing on every page.
        StatusBrush = new SolidColorBrush(Color.Parse(p.Phase switch
        {
            PvcPhase.Bound => "#34D399",
            PvcPhase.Pending => "#F5B14C",
            PvcPhase.Lost => "#F87171",
            _ => "#5C6675",
        }));

        // A Pending claim is the one that keeps a pod from starting, and the reason is almost always
        // the storage class — no provisioner, or a class name that does not exist. The row cannot know
        // which, so it points at the field instead of guessing.
        PendingHint = p.Phase == PvcPhase.Pending
            ? "Waiting to be bound. Nothing has provisioned a volume for this claim yet — open its"
              + " storage class to see why. A class that waits for a pod leaves claims here on purpose."
            : null;
    }

    public string Name { get; }
    public string Namespace { get; }
    public string Status { get; }
    public string Volume { get; }
    public string Capacity { get; }

    /// <summary>What a column sort orders CAPACITY/AGE by — the formatted text is for reading, not
    /// for comparing (KON-318).</summary>
    public long CapacityBytes { get; }

    public string StorageClass { get; }
    public string AccessModes { get; }
    public string Age { get; }
    public TimeSpan AgeSpan { get; }
    public IBrush StatusBrush { get; }

    /// <summary>Why a Pending claim is pending, as far as a list row can honestly say.</summary>
    public string? PendingHint { get; }

    public bool IsPending => PendingHint is not null;

    public bool CanOpenVolume { get; }
    public bool CanOpenClass { get; }

    [RelayCommand]
    private void OpenVolume() => _onOpenVolume?.Invoke(_volumeName);

    [RelayCommand]
    private void OpenClass() => _onOpenClass?.Invoke(_className);
}

public sealed partial class WorkloadRow
{
    private readonly Workload _workload;
    private readonly Action<Workload>? _onScale;
    private readonly Action<Workload>? _onRestart;
    private readonly Action<Workload>? _onOpenDetail;
    private readonly Action<WorkloadRow>? _onDelete;

    public WorkloadRow(
        Workload w, Action<Workload>? onScale = null, Action<Workload>? onRestart = null,
        Action<Workload>? onOpenDetail = null, Action<WorkloadRow>? onDelete = null)
    {
        _workload = w;
        _onScale = onScale;
        _onRestart = onRestart;
        _onOpenDetail = onOpenDetail;
        _onDelete = onDelete;
        CanOpen = onOpenDetail is not null;
        CanDelete = onDelete is not null;
        Reference = w.Reference;

        Name = w.Name;
        Namespace = w.Namespace;
        Kind = w.Kind.ToString();
        Ready = w.Kind == WorkloadKind.CronJob ? "—" : $"{w.Ready}/{w.Desired}";
        Schedule = w.Schedule.Length == 0 ? "—" : w.Schedule;
        Status = w.RolloutStatus.ToString();
        Age = Format.Duration(w.Age);
        AgeSpan = w.Age;
        CanScale = w.IsScalable;
        CanRestart = w.Kind is WorkloadKind.Deployment or WorkloadKind.StatefulSet or WorkloadKind.DaemonSet;
        StatusBrush = new SolidColorBrush(Color.Parse(w.RolloutStatus switch
        {
            RolloutStatus.Complete => "#34D399",
            RolloutStatus.Progressing => "#F5B14C",
            RolloutStatus.Degraded => "#F87171",
            _ => "#5C6675",
        }));
    }

    public string Name { get; }
    public string Namespace { get; }
    public string Kind { get; }
    public string Ready { get; }
    public string Schedule { get; }
    public string Status { get; }
    public string Age { get; }

    /// <summary>What the delete addresses — the kind is the row's, not the page's (KON-332).</summary>
    public ResourceRef Reference { get; }

    /// <summary>The raw age behind <see cref="Age"/> — what a column sort actually orders by (KON-318).</summary>
    public TimeSpan AgeSpan { get; }

    public bool CanScale { get; }
    public bool CanRestart { get; }
    public bool CanOpen { get; }

    /// <summary>Whether the page wired a delete handler (KON-332).</summary>
    public bool CanDelete { get; }

    public IBrush StatusBrush { get; }

    [RelayCommand]
    private void Open() => _onOpenDetail?.Invoke(_workload);

    [RelayCommand]
    private void Scale() => _onScale?.Invoke(_workload);

    [RelayCommand]
    private void Restart() => _onRestart?.Invoke(_workload);

    [RelayCommand]
    private void Delete() => _onDelete?.Invoke(this);

    /// <summary>Same rule as <see cref="PodRow"/>: equal when everything drawn is equal (KON-250).</summary>
    private string Signature =>
        string.Join('\u001f', Name, Namespace, Kind, Ready, Schedule, Status, Age);

    public override bool Equals(object? obj) =>
        obj is WorkloadRow row && string.Equals(Signature, row.Signature, StringComparison.Ordinal);

    public override int GetHashCode() => Signature.GetHashCode(StringComparison.Ordinal);
}

public sealed partial class PodRow
{
    private readonly Pod _pod;
    private readonly Action<Pod>? _open;
    private readonly Action<Pod>? _onDelete;

    public PodRow(Pod p, Action<Pod>? open = null, Action<Pod>? onDelete = null)
    {
        _pod = p;
        _open = open;
        _onDelete = onDelete;
        CanDelete = onDelete is not null;
        Name = p.Name;
        Namespace = p.Namespace;
        Ready = $"{p.ReadyContainers}/{p.Containers.Count}";
        // "Init:0/2" rather than a bare "Pending" while init containers run — the difference between a
        // pod starting up and one wedged on its first init container (KON-168).
        Phase = p.StatusText;
        PhaseRaw = p.Phase;
        Restarts = p.Restarts.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RestartsRaw = p.Restarts;
        ReadyRaw = p.ReadyContainers;
        Node = string.IsNullOrEmpty(p.Node) ? "—" : p.Node;
        Age = Format.Duration(p.Age);
        AgeSpan = p.Age;
        StatusBrush = new SolidColorBrush(Color.Parse(p.Phase switch
        {
            PodPhase.Running => "#34D399",
            PodPhase.Pending => "#F5B14C",
            PodPhase.Failed => "#F87171",
            PodPhase.Succeeded => "#5B9BD5",
            _ => "#5C6675",
        }));
    }

    public string Name { get; }
    public string Namespace { get; }
    public string Ready { get; }
    public string Phase { get; }

    /// <summary>What the STATUS filter dropdown matches on (KON-320) — <see cref="Phase"/> carries
    /// the "Init:0/2" detail this does not need.</summary>
    public PodPhase PhaseRaw { get; }

    public string Restarts { get; }
    public string Node { get; }
    public string Age { get; }
    public IBrush StatusBrush { get; }

    /// <summary>What a column sort orders READY/RESTARTS/AGE by — the formatted text is for reading,
    /// not for comparing (KON-318).</summary>
    public int ReadyRaw { get; }

    public int RestartsRaw { get; }
    public TimeSpan AgeSpan { get; }

    /// <summary>Whether the shell wired a delete handler (KON-69).</summary>
    public bool CanDelete { get; }

    [RelayCommand]
    private void Open() => _open?.Invoke(_pod);

    [RelayCommand]
    private void Delete() => _onDelete?.Invoke(_pod);

    // ── Identity for the live reconcile (KON-250) ───────────────────────────

    /// <summary>
    /// Two rows are the same row when everything they draw is the same.
    /// <para>
    /// This is what stops a reload from being visible. ListSync compares rows to decide what to add,
    /// remove and move; with reference equality a reload replaces every row with an identical-looking
    /// twin, which throws away each row's visuals and takes the scroll position with it. With this,
    /// an unchanged row is left exactly where it was and only the pod that actually changed is
    /// redrawn.
    /// </para>
    /// </summary>
    private string Signature =>
        string.Join('\u001f', Name, Namespace, Ready, Phase, Restarts, Node, Age);

    public override bool Equals(object? obj) =>
        obj is PodRow row && string.Equals(Signature, row.Signature, StringComparison.Ordinal);

    public override int GetHashCode() => Signature.GetHashCode(StringComparison.Ordinal);
}

public sealed partial class ServiceRow
{
    private readonly Service _service;
    private readonly Action<Service>? _onForward;
    private readonly Action<Service>? _onOpenDetail;
    private readonly Action<ServiceRow>? _onDelete;

    public ServiceRow(
        Service s, Action<Service>? onForward = null, Action<Service>? onOpenDetail = null,
        Action<ServiceRow>? onDelete = null)
    {
        _service = s;
        _onForward = onForward;
        _onOpenDetail = onOpenDetail;
        _onDelete = onDelete;
        CanOpen = onOpenDetail is not null;
        CanDelete = onDelete is not null;
        Reference = new ResourceRef(GroupVersionKind.Service, s.Namespace, s.Name);
        IsLoadBalancer = s.Type == ServiceType.LoadBalancer;

        Name = s.Name;
        Namespace = s.Namespace;
        Type = s.Type.ToString();
        ClusterIp = string.IsNullOrEmpty(s.ClusterIp) ? "—" : s.ClusterIp;
        // Same as the pod-detail containers table: one list, joined in the cell and stacked in the
        // tooltip, because a service with several ports trims and the rest has to be reachable (KON-199).
        var ports = s.Ports
            .Select(p => p.NodePort is int np ? $"{p.Port}:{np}/{p.Protocol}" : $"{p.Port}/{p.Protocol}")
            .ToList();
        Ports = ports.Count == 0 ? "—" : string.Join("  ", ports);
        PortsTooltip = ports.Count == 0 ? null : string.Join("\n", ports);
        Age = Format.Duration(s.Age);
        AgeSpan = s.Age;
        CanForward = s.Ports.Count > 0;
    }

    public string Name { get; }
    public string Namespace { get; }
    public string Type { get; }
    public string ClusterIp { get; }
    public string Ports { get; }
    public string? PortsTooltip { get; }
    public string Age { get; }

    /// <summary>What the delete addresses (KON-332).</summary>
    public ResourceRef Reference { get; }

    /// <summary>
    /// Whether deleting this one also gives up an external address — the part of a service delete
    /// that cannot be undone by applying the same manifest again (KON-332).
    /// </summary>
    public bool IsLoadBalancer { get; }

    /// <summary>The raw age behind <see cref="Age"/> — what a column sort actually orders by (KON-318).</summary>
    public TimeSpan AgeSpan { get; }

    public bool CanForward { get; }
    public bool CanOpen { get; }

    /// <summary>Whether the page wired a delete handler (KON-332).</summary>
    public bool CanDelete { get; }

    [RelayCommand]
    private void Open() => _onOpenDetail?.Invoke(_service);

    [RelayCommand]
    private void Forward() => _onForward?.Invoke(_service);

    [RelayCommand]
    private void Delete() => _onDelete?.Invoke(this);

    /// <summary>Same rule as <see cref="PodRow"/>: equal when everything drawn is equal (KON-250).</summary>
    private string Signature => string.Join('\u001f', Name, Namespace, Type, ClusterIp, Ports, Age);

    public override bool Equals(object? obj) =>
        obj is ServiceRow row && string.Equals(Signature, row.Signature, StringComparison.Ordinal);

    public override int GetHashCode() => Signature.GetHashCode(StringComparison.Ordinal);
}

/// <summary>
/// What a confirm says before deleting a workload, a service or an ingress (KON-332), in one place
/// because two places say it (KON-334): the list row and the detail page of the same object.
/// <para>
/// Pulled out for the same reason as <c>ConfigDelete</c>: the delete itself is one call, the wording
/// is the whole feature, and a sentence written twice about the same act is a sentence that will
/// disagree with itself the first time either copy is improved. The mechanism differs by where you
/// are — a list page raises its own confirm, a detail page goes through the shell so the drawer can
/// close and the history step can go — so this returns the words rather than raising anything.
/// </para>
/// </summary>
internal static class ClusterDeleteWording
{
    /// <summary>
    /// Kind-aware on purpose. "Delete" on a controller is not the same act on every kind: a
    /// StatefulSet leaves its volume claims behind, a CronJob takes a schedule with it, and a
    /// DaemonSet's pods are on every node rather than in one place. One sentence for all five would
    /// have to be vague about exactly the part someone is deciding on.
    /// </summary>
    public static (string Title, string Message) Workload(string kind, string name, string @namespace)
    {
        var consequence = kind switch
        {
            "CronJob" => "Its schedule stops, and the jobs it already created go with it.",
            "Job" => "Its pods go with it, the finished ones included.",
            "StatefulSet" => "Its pods are terminated with it. The volume claims it made are not —"
                + " those stay, and a StatefulSet applied again under this name picks them back up.",
            "DaemonSet" => "Its pods are terminated on every node that runs one.",
            _ => "Its pods are terminated with it and nothing recreates them.",
        };

        return ($"Delete {kind}",
            $"Delete {kind} \"{name}\" in {@namespace}? {consequence} Kontena keeps no copy, so"
            + " bringing it back means applying its manifest again.");
    }

    /// <summary>
    /// What breaks is one step away from what is deleted — the pods keep running and keep looking
    /// healthy, and it is everything that reached them by name that stops. A LoadBalancer adds the
    /// part that re-applying the same manifest does not undo: the address goes back to the provider.
    /// </summary>
    public static (string Title, string Message) Service(string name, string @namespace, bool loadBalancer)
    {
        var address = loadBalancer
            ? " Its external address is released, and a service created again does not get the same one back."
            : string.Empty;

        return ("Delete service",
            $"Delete service \"{name}\" in {@namespace}? The pods behind it keep running, but nothing"
            + " reaches them by this name any more: clients in the cluster stop resolving it, and any"
            + $" ingress routing to it starts failing.{address}");
    }

    /// <summary>
    /// The smallest blast radius of the three and the one most likely to be misread as bigger:
    /// nothing inside the cluster changes, and what stops is the way in from outside.
    /// </summary>
    public static (string Title, string Message) Ingress(string name, string @namespace) =>
        ("Delete ingress",
            $"Delete ingress \"{name}\" in {@namespace}? The service and its pods keep running — what"
            + " goes is the route in from outside, so the hosts it routes stop reaching them as soon as"
            + " the controller drops the rule.");
}
