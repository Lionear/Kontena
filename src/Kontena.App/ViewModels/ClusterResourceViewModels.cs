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
public partial class ClusterNodesViewModel : ListPageViewModel<NodeCardRow>
{
    private readonly IClusterEngine _cluster;

    public ClusterNodesViewModel(IClusterEngine cluster)
    {
        _cluster = cluster;

        // Plenty of clusters (kind, plain kubeadm) ship without a usage backend. Rather than
        // leaving four dashes unexplained, say so once and say what would fix it.
        ShowMetricsNotice = !cluster.Capabilities.Metrics;
        MetricsNoticeDetail = cluster is IMetricsAware { Metrics.Name: var source } && source != "none"
            ? $"Kontena looked for {source} and it did not answer."
            : "This cluster has no usage backend configured.";

        _ = LoadAsync();
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

    protected override async Task<IReadOnlyList<NodeCardRow>> LoadRowsAsync()
    {
        // The apiserver version is what a kubelet version means anything against (KON-95): a node is
        // only "behind" relative to its own control plane.
        var info = await _cluster.GetInfoAsync();
        _context = info is ClusterInfo { Context: { Length: > 0 } context } ? context : string.Empty;

        return [.. (await _cluster.ListNodesAsync()).Select(n => new NodeCardRow(n, info.Version))];
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
public partial class ClusterNamespacesViewModel : ListPageViewModel<NamespaceRow>
{
    private readonly IClusterEngine _cluster;

    public ClusterNamespacesViewModel(IClusterEngine cluster)
    {
        _cluster = cluster;
        _ = LoadAsync();
    }

    public override string SearchPlaceholder => "Search namespaces…";

    protected override async Task<IReadOnlyList<NamespaceRow>> LoadRowsAsync() =>
        [.. (await _cluster.ListNamespacesAsync()).Select(ns => new NamespaceRow(ns.Name, ns.Phase, Format.Duration(ns.Age)))];

    protected override bool Matches(NamespaceRow row, string term) => Contains(row.Name, term);
}

/// <summary>Workloads view — the controllers (Deployment/StatefulSet/DaemonSet/Job/CronJob).</summary>
public partial class ClusterWorkloadsViewModel : ListPageViewModel<WorkloadRow>
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
    {
        _cluster = cluster;
        _namespace = @namespace;
        _onScale = onScale;
        _onRestart = onRestart;
        _onOpenDetail = onOpenDetail;
        _kind = kind;
        _ = LoadAsync();
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

    /// <summary>Shown when a kind's page is empty, so it does not look like a failed load.</summary>
    public string EmptyText => _kind is { } k
        ? $"No {k}s in this namespace."
        : "No workloads in this namespace.";

    protected override async Task<IReadOnlyList<WorkloadRow>> LoadRowsAsync() =>
        [.. (await _cluster.ListWorkloadsAsync(_kind, _namespace))
            .Select(w => new WorkloadRow(w, _onScale, _onRestart, _onOpenDetail))];

    protected override bool Matches(WorkloadRow row, string term) =>
        Contains(row.Name, term) || Contains(row.Kind, term) || Contains(row.Namespace, term);
}

/// <summary>Pods view.</summary>
public partial class ClusterPodsViewModel : ListPageViewModel<PodRow>
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
    {
        _cluster = cluster;
        _namespace = @namespace;
        _onOpenDetail = onOpenDetail;
        _onDelete = onDelete;
        _ = LoadAsync();
    }

    public override string SearchPlaceholder => "Search pods…";

    protected override async Task<IReadOnlyList<PodRow>> LoadRowsAsync() =>
        [.. (await _cluster.ListPodsAsync(_namespace)).Select(p => new PodRow(p, _onOpenDetail, _onDelete))];

    // Node and status too: "which pods are on worker-2" and "what is CrashLooping" are the two
    // questions a pod list gets asked.
    protected override bool Matches(PodRow row, string term) =>
        Contains(row.Name, term) || Contains(row.Namespace, term)
        || Contains(row.Node, term) || Contains(row.Phase, term);
}

/// <summary>Services view.</summary>
public partial class ClusterServicesViewModel : ListPageViewModel<ServiceRow>
{
    private readonly IClusterEngine _cluster;
    private readonly string? _namespace;

    private readonly Action<Service>? _onForward;
    private readonly Action<Service>? _onOpenDetail;

    public ClusterServicesViewModel(
        IClusterEngine cluster, string? @namespace,
        Action<Service>? onForward = null, Action<Service>? onOpenDetail = null)
    {
        _cluster = cluster;
        _namespace = @namespace;
        _onForward = onForward;
        _onOpenDetail = onOpenDetail;
        _ = LoadAsync();
    }

    public override string SearchPlaceholder => "Search services…";

    protected override async Task<IReadOnlyList<ServiceRow>> LoadRowsAsync() =>
        [.. (await _cluster.ListServicesAsync(_namespace)).Select(s => new ServiceRow(s, _onForward, _onOpenDetail))];

    protected override bool Matches(ServiceRow row, string term) =>
        Contains(row.Name, term) || Contains(row.Namespace, term)
        || Contains(row.Type, term) || Contains(row.Ports, term);
}

// ── Row view-models ─────────────────────────────────────────────────────────

public sealed class NodeCardRow
{
    public NodeCardRow(Node n, string? apiServerVersion = null)
    {
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

public sealed record NamespaceRow(string Name, string Status, string Age);

public sealed partial class WorkloadRow
{
    private readonly Workload _workload;
    private readonly Action<Workload>? _onScale;
    private readonly Action<Workload>? _onRestart;
    private readonly Action<Workload>? _onOpenDetail;

    public WorkloadRow(
        Workload w, Action<Workload>? onScale = null, Action<Workload>? onRestart = null,
        Action<Workload>? onOpenDetail = null)
    {
        _workload = w;
        _onScale = onScale;
        _onRestart = onRestart;
        _onOpenDetail = onOpenDetail;
        CanOpen = onOpenDetail is not null;

        Name = w.Name;
        Namespace = w.Namespace;
        Kind = w.Kind.ToString();
        Ready = w.Kind == WorkloadKind.CronJob ? "—" : $"{w.Ready}/{w.Desired}";
        Schedule = w.Schedule.Length == 0 ? "—" : w.Schedule;
        Status = w.RolloutStatus.ToString();
        Age = Format.Duration(w.Age);
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
    public bool CanScale { get; }
    public bool CanRestart { get; }
    public bool CanOpen { get; }
    public IBrush StatusBrush { get; }

    [RelayCommand]
    private void Open() => _onOpenDetail?.Invoke(_workload);

    [RelayCommand]
    private void Scale() => _onScale?.Invoke(_workload);

    [RelayCommand]
    private void Restart() => _onRestart?.Invoke(_workload);
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
        Restarts = p.Restarts.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Node = string.IsNullOrEmpty(p.Node) ? "—" : p.Node;
        Age = Format.Duration(p.Age);
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
    public string Restarts { get; }
    public string Node { get; }
    public string Age { get; }
    public IBrush StatusBrush { get; }

    /// <summary>Whether the shell wired a delete handler (KON-69).</summary>
    public bool CanDelete { get; }

    [RelayCommand]
    private void Open() => _open?.Invoke(_pod);

    [RelayCommand]
    private void Delete() => _onDelete?.Invoke(_pod);
}

public sealed partial class ServiceRow
{
    private readonly Service _service;
    private readonly Action<Service>? _onForward;
    private readonly Action<Service>? _onOpenDetail;

    public ServiceRow(Service s, Action<Service>? onForward = null, Action<Service>? onOpenDetail = null)
    {
        _service = s;
        _onForward = onForward;
        _onOpenDetail = onOpenDetail;
        CanOpen = onOpenDetail is not null;

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
        CanForward = s.Ports.Count > 0;
    }

    public string Name { get; }
    public string Namespace { get; }
    public string Type { get; }
    public string ClusterIp { get; }
    public string Ports { get; }
    public string? PortsTooltip { get; }
    public string Age { get; }
    public bool CanForward { get; }
    public bool CanOpen { get; }

    [RelayCommand]
    private void Open() => _onOpenDetail?.Invoke(_service);

    [RelayCommand]
    private void Forward() => _onForward?.Invoke(_service);
}
