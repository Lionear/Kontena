using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Orchestration;
using Kontena.Core.Orchestration.Models;

namespace Kontena.App.ViewModels;

// The cluster resource browsers behind the k8s nav (KON-73). Each reads its list from an
// IClusterEngine — the FakeClusterEngine today, a real adapter (KON-68) later — and filters by
// the command-bar namespace picker where namespaces apply. Pod/workload detail, actions, and the
// apply flow are their own tickets (KON-69/70/71).

/// <summary>Nodes view — a card per node with CPU/memory gauges (see k8s-nodes mockup).</summary>
public partial class ClusterNodesViewModel : ViewModelBase
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

    public ObservableCollection<NodeCardRow> Nodes { get; } = [];

    /// <summary>Whether to explain the missing CPU/memory gauges.</summary>
    public bool ShowMetricsNotice { get; }

    /// <summary>Which source was tried, when the backend can tell us.</summary>
    public string MetricsNoticeDetail { get; }

    /// <summary>
    /// What the user can do about it. Installing a metrics-server from Kontena is its own ticket
    /// (KON-93); for now this is guidance, not an action.
    /// </summary>
    public string MetricsNoticeAction { get; } =
        "Install metrics-server in the cluster to enable them. Node status, conditions and pod counts" +
        " do not need a metrics source and are unaffected.";

    private async Task LoadAsync()
    {
        Nodes.Clear();
        foreach (var n in await _cluster.ListNodesAsync())
            Nodes.Add(new NodeCardRow(n));
    }
}

/// <summary>Namespaces view.</summary>
public partial class ClusterNamespacesViewModel : ViewModelBase
{
    private readonly IClusterEngine _cluster;

    public ClusterNamespacesViewModel(IClusterEngine cluster)
    {
        _cluster = cluster;
        _ = LoadAsync();
    }

    public ObservableCollection<NamespaceRow> Namespaces { get; } = [];

    private async Task LoadAsync()
    {
        Namespaces.Clear();
        foreach (var ns in await _cluster.ListNamespacesAsync())
            Namespaces.Add(new NamespaceRow(ns.Name, ns.Phase, Format.Duration(ns.Age)));
    }
}

/// <summary>Workloads view — the controllers (Deployment/StatefulSet/DaemonSet/Job/CronJob).</summary>
public partial class ClusterWorkloadsViewModel : ViewModelBase
{
    private readonly IClusterEngine _cluster;
    private readonly string? _namespace;
    private readonly Action<Workload>? _onScale;
    private readonly Action<Workload>? _onRestart;

    public ClusterWorkloadsViewModel(
        IClusterEngine cluster, string? @namespace,
        Action<Workload>? onScale = null, Action<Workload>? onRestart = null)
    {
        _cluster = cluster;
        _namespace = @namespace;
        _onScale = onScale;
        _onRestart = onRestart;
        _ = LoadAsync();
    }

    public ObservableCollection<WorkloadRow> Workloads { get; } = [];

    private async Task LoadAsync()
    {
        Workloads.Clear();
        foreach (var w in await _cluster.ListWorkloadsAsync(null, _namespace))
            Workloads.Add(new WorkloadRow(w, _onScale, _onRestart));
    }
}

/// <summary>Pods view.</summary>
public partial class ClusterPodsViewModel : ViewModelBase
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

    public ObservableCollection<PodRow> Pods { get; } = [];

    private async Task LoadAsync()
    {
        Pods.Clear();
        foreach (var p in await _cluster.ListPodsAsync(_namespace))
            Pods.Add(new PodRow(p, _onOpenDetail, _onDelete));
    }
}

/// <summary>Services view.</summary>
public partial class ClusterServicesViewModel : ViewModelBase
{
    private readonly IClusterEngine _cluster;
    private readonly string? _namespace;

    private readonly Action<Service>? _onForward;

    public ClusterServicesViewModel(IClusterEngine cluster, string? @namespace, Action<Service>? onForward = null)
    {
        _cluster = cluster;
        _namespace = @namespace;
        _onForward = onForward;
        _ = LoadAsync();
    }

    public ObservableCollection<ServiceRow> Services { get; } = [];

    private async Task LoadAsync()
    {
        Services.Clear();
        foreach (var s in await _cluster.ListServicesAsync(_namespace))
            Services.Add(new ServiceRow(s, _onForward));
    }
}

// ── Row view-models ─────────────────────────────────────────────────────────

public sealed class NodeCardRow
{
    public NodeCardRow(Node n)
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

        // Pod counts come off the pod list, so they show even when there is no metrics source.
        PodsText = $"{n.ScheduledPods} / {cap.Pods}";

        // Conditions need no metrics source either. Only the failing ones are worth surfacing —
        // a healthy node's five green conditions are noise, and the Ready dot already says it.
        Problems = [.. n.Problems.Select(c => new NodeProblemChip(c))];
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

    /// <summary>Conditions currently signalling trouble; empty on a healthy node.</summary>
    public IReadOnlyList<NodeProblemChip> Problems { get; }

    public bool HasProblems => Problems.Count > 0;

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

    public WorkloadRow(Workload w, Action<Workload>? onScale = null, Action<Workload>? onRestart = null)
    {
        _workload = w;
        _onScale = onScale;
        _onRestart = onRestart;

        Name = w.Name;
        Namespace = w.Namespace;
        Kind = w.Kind.ToString();
        Ready = $"{w.Ready}/{w.Desired}";
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
    public string Status { get; }
    public string Age { get; }
    public bool CanScale { get; }
    public bool CanRestart { get; }
    public IBrush StatusBrush { get; }

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
        Phase = p.Phase.ToString();
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

    public ServiceRow(Service s, Action<Service>? onForward = null)
    {
        _service = s;
        _onForward = onForward;

        Name = s.Name;
        Namespace = s.Namespace;
        Type = s.Type.ToString();
        ClusterIp = string.IsNullOrEmpty(s.ClusterIp) ? "—" : s.ClusterIp;
        Ports = s.Ports.Count == 0
            ? "—"
            : string.Join("  ", s.Ports.Select(p => p.NodePort is int np ? $"{p.Port}:{np}/{p.Protocol}" : $"{p.Port}/{p.Protocol}"));
        Age = Format.Duration(s.Age);
        CanForward = s.Ports.Count > 0;
    }

    public string Name { get; }
    public string Namespace { get; }
    public string Type { get; }
    public string ClusterIp { get; }
    public string Ports { get; }
    public string Age { get; }
    public bool CanForward { get; }

    [RelayCommand]
    private void Forward() => _onForward?.Invoke(_service);
}
