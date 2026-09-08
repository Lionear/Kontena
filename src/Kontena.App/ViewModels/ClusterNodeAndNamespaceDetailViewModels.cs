using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.App.Controls;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;
using Kontena.Core.Orchestration;

namespace Kontena.App.ViewModels;

// Node and Namespace detail (KON-197). Both were dead ends: the grids showed them and there was
// nowhere to go, which for a node meant the conditions were summarised to a chip and never
// readable in full, and for a namespace meant no way to ask what was actually in it.

/// <summary>
/// One node: what it is, how it is doing, everything running on it, and the two things you can do
/// to it.
/// </summary>
public sealed partial class ClusterNodeDetailViewModel : ClusterObjectDetailViewModel
{
    private readonly Action<string>? _onDrain;
    private readonly Func<string, bool, Task>? _onCordon;
    private readonly string? _apiServerVersion;
    private Node _node;

    public ClusterNodeDetailViewModel(
        IClusterEngine cluster, Node node, string? apiServerVersion,
        Action<Pod>? onOpenPod = null, Func<string, bool, Task>? onCordon = null,
        Action<string>? onDrain = null)
        : base(cluster, new ResourceRef(GroupVersionKind.Node, null, node.Name), onOpenPod)
    {
        ArgumentNullException.ThrowIfNull(node);

        _onCordon = onCordon;
        _onDrain = onDrain;
        _apiServerVersion = apiServerVersion;
        _node = node;
        _cordoned = node.Unschedulable;

        CanMaintain = cluster.Capabilities.NodeMaintenance && onCordon is not null;

        // Disk gets a chart here and nowhere else: the kubelet is the only source that reports it,
        // and a node is the only thing it is reported for. No history metric for any of the three —
        // node-exporter keys its series by scrape address, not by node name (KON-347).
        // Allocatable is the ceiling a node is read against, and the one number that turns "8.1
        // cores" into "8.1 of 16". Read once, unlike the header fields: a chart's axis is fixed when
        // the track is built, and a node's allocatable capacity does not move under a running page.
        var cap = node.Capacity;

        List<UsageChartSpec> charts =
        [
            new("CPU", UsageChartUnit.Millicores, "Primary", UsageMetric.Cpu, "millicores",
                cap.CpuMillicores > 0 ? cap.CpuMillicores : null,
                cap.CpuMillicores > 0 ? $"allocatable {cap.CpuMillicores}m" : null),
            new("Memory", UsageChartUnit.Bytes, "Accent", UsageMetric.Memory, "working set",
                cap.MemoryBytes > 0 ? cap.MemoryBytes : null,
                cap.MemoryBytes > 0 ? $"allocatable {Format.Size(cap.MemoryBytes)}" : null),
        ];

        // Disk has no stored series: only the kubelet reports it, so it stays on the live buffer
        // and its own axis says so.
        if (node.Usage?.DiskUsedBytes is not null)
            charts.Add(new UsageChartSpec("Disk", UsageChartUnit.Bytes, "Info", null, "used"));

        ConfigureUsage(charts, UsageTarget.Node(node.Name),
            caveat: "Node history comes from node-exporter, which counts memory as total minus "
                    + "available where the kubelet reports a working set. Expect roughly a tenth of "
                    + "a difference from the live figure above.",
            sample: async ct =>
        {
            if (cluster is not IMetricsAware aware)
                return null;

            var usage = await aware.Metrics.GetNodeUsageAsync(ct).ConfigureAwait(false);
            if (!usage.TryGetValue(node.Name, out var mine))
                return null;

            return charts.Count == 3
                ? [mine.CpuMillicores, mine.MemoryBytes, mine.DiskUsedBytes ?? 0]
                : [mine.CpuMillicores, mine.MemoryBytes];
            });

        _ = LoadPodsAsync();
    }

    // Computed off the node rather than copied out of it in the constructor (KON-450), so that
    // re-reading the node is enough to redraw the page. Every one of these is a field the kubelet
    // moves on its own: a node going NotReady while you are looking at it is the exact reason this
    // page is open.
    public string Roles => _node.Roles.Count > 0 ? string.Join(", ", _node.Roles) : "—";
    public string Status => _node.Status;
    public string KubeletVersion => _node.KubeletVersion;
    public string OsImage => string.IsNullOrEmpty(_node.OsImage) ? "—" : _node.OsImage;
    public string InternalIp => string.IsNullOrEmpty(_node.InternalIp) ? "—" : _node.InternalIp;
    public string Age => Format.Duration(_node.Age);

    public string Cpu => _node.Usage is null
        ? $"{_node.Capacity.CpuMillicores}m allocatable"
        : $"{_node.Usage.CpuMillicores}m of {_node.Capacity.CpuMillicores}m";

    public string Memory => _node.Usage is null
        ? $"{Format.Size(_node.Capacity.MemoryBytes)} allocatable"
        : $"{Format.Size(_node.Usage.MemoryBytes)} of {Format.Size(_node.Capacity.MemoryBytes)}";

    /// <summary>Scheduled against allocatable — named apart from the base's pod list.</summary>
    public string PodCapacity => $"{_node.ScheduledPods} of {_node.Capacity.Pods}";

    /// <summary>
    /// Every condition, not only the failing ones. The card shows the problems because a card has
    /// room for what is wrong; this page is where you come to read what the kubelet actually says,
    /// and a healthy MemoryPressure is a fact you sometimes need to see stated.
    /// </summary>
    public IReadOnlyList<NodeConditionRow> Conditions =>
        [.. _node.Conditions.Select(c => new NodeConditionRow(c))];

    public IBrush StatusBrush => new SolidColorBrush(Color.Parse(Status == "Ready" ? "#34D399" : "#F87171"));

    public NodeVersionSkew Skew => VersionSkewPolicy.Evaluate(_apiServerVersion, _node.KubeletVersion);
    public bool HasVersionWarning => Skew.IsProblem;
    public string VersionWarning => Skew.Summary;
    public string VersionWarningDetail => Skew.Detail;

    // ── Maintenance ─────────────────────────────────────────────────────────

    public bool CanMaintain { get; }

    [ObservableProperty] private bool _cordoned;

    partial void OnCordonedChanged(bool value)
    {
        OnPropertyChanged(nameof(CordonLabel));
        OnPropertyChanged(nameof(CordonState));
    }

    public string CordonLabel => Cordoned ? "Uncordon" : "Cordon";

    /// <summary>
    /// Said in full on a page that has the room for it: a card can only afford the word "Cordoned",
    /// which does not distinguish a node someone is about to work on from one that was forgotten.
    /// </summary>
    public string CordonState => Cordoned
        ? "Cordoned — nothing new is being scheduled here. The pods already running are untouched."
        : "Schedulable — new pods can land here.";

    [RelayCommand]
    private async Task ToggleCordonAsync()
    {
        if (_onCordon is null)
            return;

        var wanted = !Cordoned;
        await _onCordon(Name, wanted);
        Cordoned = wanted;
    }

    [RelayCommand]
    private void Drain() => _onDrain?.Invoke(Name);

    // ── The base's hooks ────────────────────────────────────────────────────

    /// <summary>
    /// Follow this node (KON-450). Its status, its conditions and its kubelet version are all things
    /// the cluster changes without anyone here asking, and the page showed whichever of them were
    /// true when it was opened.
    /// <para>
    /// Read <b>without</b> pod counts. On Kubernetes that flag is a second, cluster-wide read — every
    /// pod on the cluster, for one integer (KON-395) — and a Node event says the node changed, not
    /// that pods moved. The count already on the page is carried across rather than re-bought on
    /// every kubelet heartbeat; it goes stale, and that is the cheaper of the two wrong answers.
    /// </para>
    /// </summary>
    protected override Task OnResourceModifiedAsync() => RefreshAsync();

    /// <inheritdoc cref="OnResourceModifiedAsync"/>
    public async Task RefreshAsync()
    {
        var fresh = await RefetchAsync(
            () => Cluster.ListNodesAsync(withPodCounts: false),
            n => n.Name == Name);

        if (fresh is null)
            return;

        _node = fresh with { ScheduledPods = _node.ScheduledPods };

        OnPropertyChanged(nameof(Roles));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(KubeletVersion));
        OnPropertyChanged(nameof(OsImage));
        OnPropertyChanged(nameof(InternalIp));
        OnPropertyChanged(nameof(Age));
        OnPropertyChanged(nameof(Cpu));
        OnPropertyChanged(nameof(Memory));
        OnPropertyChanged(nameof(PodCapacity));
        OnPropertyChanged(nameof(Conditions));
        OnPropertyChanged(nameof(Skew));
        OnPropertyChanged(nameof(HasVersionWarning));
        OnPropertyChanged(nameof(VersionWarning));
        OnPropertyChanged(nameof(VersionWarningDetail));

        // The page's own Cordon button already sets this; this is the other way it changes — someone
        // else, or kubectl, cordoning the node while this page is open.
        Cordoned = fresh.Unschedulable;
    }

    /// <summary>A node's pods are spread across every namespace there is.</summary>
    protected override string? Scope => null;

    public override string PodsTabLabel => "Pods on this node";

    protected override IReadOnlyList<Pod> SelectPods(IReadOnlyList<Pod> all) =>
        [.. all.Where(p => p.Node == Name)];

    protected override string EmptyPodsReason() => Cordoned
        ? "Nothing is running here. The node is cordoned, so nothing new will arrive either."
        : "Nothing is running on this node.";
}

/// <summary>One reported condition of a node, healthy ones included.</summary>
public sealed class NodeConditionRow
{
    public NodeConditionRow(NodeCondition condition)
    {
        Type = condition.Type;
        State = condition.IsActive ? "True" : "False";
        Detail = string.IsNullOrEmpty(condition.Message) ? condition.Reason : condition.Message;

        // Ready is the one condition where True is the good answer and every pressure condition is
        // the other way round — so the colour follows IsProblem, not the raw boolean.
        Brush = new SolidColorBrush(Color.Parse(condition.IsProblem ? "#F5B14C" : "#34D399"));
    }

    public string Type { get; }
    public string State { get; }
    public string Detail { get; }
    public IBrush Brush { get; }
}

/// <summary>
/// One namespace: what it holds. The list answered "does it exist"; this answers the question you
/// actually had, which is what is inside and whether it is safe to be rid of.
/// </summary>
public sealed partial class ClusterNamespaceDetailViewModel : ClusterObjectDetailViewModel
{
    private readonly IClusterEngine _cluster;
    private readonly Action<string, string>? _onOpenKind;
    private KubeNamespace _ns;

    /// <param name="onOpenKind">Navigates to a list page, scoped to this namespace.</param>
    public ClusterNamespaceDetailViewModel(
        IClusterEngine cluster, KubeNamespace ns,
        Action<Pod>? onOpenPod = null, Action<string, string>? onOpenKind = null)
        : base(cluster, new ResourceRef(GroupVersionKind.Namespace, null, ns.Name), onOpenPod)
    {
        ArgumentNullException.ThrowIfNull(ns);

        _cluster = cluster;
        _onOpenKind = onOpenKind;
        _ns = ns;

        // Everything in the namespace, summed. Live from the pod snapshot the metrics source
        // already answers with, and from Prometheus for the longer ranges (KON-347).
        ConfigureUsage(
            [
                new UsageChartSpec("CPU", UsageChartUnit.Millicores, "Primary", UsageMetric.Cpu, "millicores"),
                new UsageChartSpec("Memory", UsageChartUnit.Bytes, "Accent", UsageMetric.Memory, "working set"),
            ],
            UsageTarget.Namespaced(ns.Name),
            async ct =>
            {
                if (cluster is not IMetricsAware aware)
                    return null;

                var pods = await aware.Metrics.GetPodUsageAsync(ns.Name, ct).ConfigureAwait(false);
                return pods.Count == 0
                    ? null
                    : [pods.Sum(p => (double)p.CpuMillicores), pods.Sum(p => (double)p.MemoryBytes)];
            });

        _ = LoadContentsAsync();
        _ = LoadPodsAsync();
    }

    // Computed off the namespace rather than copied out of it (KON-450), so re-reading it redraws
    // the page. Phase is the whole reason: a namespace has exactly one interesting state change, and
    // it is this one.
    public string Phase => _ns.Phase;
    public string Age => Format.Duration(_ns.Age);
    public string Labels => FormatLabels(_ns.Labels);

    /// <summary>
    /// Terminating is not a state you wait out cheerfully: a namespace stuck there is nearly always
    /// a finalizer on something inside it, and that is worth saying where it is seen.
    /// </summary>
    public bool IsTerminating => string.Equals(_ns.Phase, "Terminating", StringComparison.Ordinal);

    public string TerminatingNote { get; } =
        "This namespace is being deleted and has not finished. A namespace that stays here is almost"
        + " always waiting on a finalizer belonging to something inside it.";

    public IBrush PhaseBrush =>
        new SolidColorBrush(Color.Parse(IsTerminating ? "#F5B14C" : "#34D399"));

    /// <summary>What is in here, by kind, each a way in.</summary>
    public ObservableCollection<NamespaceContentRow> Contents { get; } = [];

    [ObservableProperty] private bool _contentsLoading;

    /// <summary>
    /// What is in here that someone put there. Kubernetes creates a <c>kube-root-ca.crt</c> config
    /// map in every namespace and a token beside every service account, so counting those would mean
    /// no namespace is ever empty — and emptiness is the whole question this page is opened with.
    /// </summary>
    private int _ownContents;

    /// <summary>The config map the API server writes into every namespace, since Kubernetes 1.20.</summary>
    private const string RootCaConfigMap = "kube-root-ca.crt";

    /// <summary>The secret type Kubernetes mints beside a service account.</summary>
    private const string ServiceAccountTokenSecret = "kubernetes.io/service-account-token";

    /// <summary>
    /// Whether the contents could be read at all. A namespace whose contents were refused is not an
    /// empty namespace, and saying so would be the most confident possible way to be wrong.
    /// </summary>
    private bool _contentsFailed;

    public bool IsEmptyNamespace => !ContentsLoading && !_contentsFailed && _ownContents == 0;

    /// <summary>
    /// A namespace with nothing in it is worth stating outright — it is the answer to "can I delete
    /// this", and a page of seven zeroes makes you count them yourself. It names what it is ignoring,
    /// because "nothing is in this namespace" beside a config map count of 1 reads as a contradiction.
    /// </summary>
    public string EmptyNote { get; } =
        "Nothing is in this namespace, apart from what Kubernetes puts in every one.";

    private async Task LoadContentsAsync()
    {
        ContentsLoading = true;

        try
        {
            // One namespace, seven questions, asked together rather than in sequence: they are
            // independent and the page is not readable until the last of them lands anyway.
            var workloads = _cluster.ListWorkloadsAsync(null, Name);
            var pods = _cluster.ListPodsAsync(Name);
            var services = _cluster.ListServicesAsync(Name);
            var ingresses = _cluster.ListIngressesAsync(Name);
            var claims = _cluster.ListPvcsAsync(Name);

            // Config maps and secrets count too, and leaving them out was not a missing row but a
            // wrong answer: this tally is what "can I delete this namespace?" is asked with, and a
            // namespace holding nothing but secrets read as empty.
            var configMaps = _cluster.ListConfigMapsAsync(Name);
            var secrets = _cluster.ListSecretsAsync(Name);

            var maps = await configMaps;
            var keys = await secrets;

            Add("Workloads", (await workloads).Count, "workloads");
            Add("Pods", (await pods).Count, "pods");
            Add("Services", (await services).Count, "services");
            Add("Ingresses", (await ingresses).Count, "ingresses");
            Add("Volume claims", (await claims).Count, "pvcs");
            Add("Config maps", maps.Count, "configmaps");
            Add("Secrets", keys.Count, "secrets");

            // The rows carry the real counts; emptiness is measured against what you would actually
            // lose. See _ownContents for why those are not the same number.
            _ownContents = Contents.Where(c => c.Label is not ("Config maps" or "Secrets")).Sum(c => c.Count)
                + maps.Count(m => !string.Equals(m.Name, RootCaConfigMap, StringComparison.Ordinal))
                + keys.Count(s => !string.Equals(s.Type, ServiceAccountTokenSecret, StringComparison.Ordinal));
        }
        catch (Exception)
        {
            // A namespace you cannot read the contents of still has a name, a phase and its labels,
            // and that is most of why this page was opened.
            Contents.Clear();
            _contentsFailed = true;
        }
        finally
        {
            ContentsLoading = false;
            OnPropertyChanged(nameof(IsEmptyNamespace));
        }
    }

    private void Add(string label, int count, string navKey) =>
        Contents.Add(new NamespaceContentRow(label, count, navKey, _onOpenKind, Name));

    /// <summary>
    /// Follow this namespace (KON-450). Only its own fields: a Namespace event means the namespace
    /// object changed, and the contents tally below is a different question with seven reads behind
    /// it — re-running that here would bill a namespace's own phase change for a survey of everything
    /// inside it. The tally moves when the things in it move, which is not this event.
    /// </summary>
    protected override Task OnResourceModifiedAsync() => RefreshAsync();

    /// <inheritdoc cref="OnResourceModifiedAsync"/>
    public async Task RefreshAsync()
    {
        var fresh = await RefetchAsync(
            () => Cluster.ListNamespacesAsync(),
            n => n.Name == Name);

        if (fresh is null)
            return;

        _ns = fresh;

        OnPropertyChanged(nameof(Phase));
        OnPropertyChanged(nameof(PhaseBrush));
        OnPropertyChanged(nameof(Age));
        OnPropertyChanged(nameof(Labels));
        OnPropertyChanged(nameof(IsTerminating));
    }

    protected override string? Scope => Name;

    public override string PodsTabLabel => "Pods";

    protected override IReadOnlyList<Pod> SelectPods(IReadOnlyList<Pod> all) => all;

    protected override string EmptyPodsReason() => "No pods in this namespace.";
}

/// <summary>One kind's tally inside a namespace, and the way through to it.</summary>
public sealed partial class NamespaceContentRow
{
    private readonly Action<string, string>? _open;
    private readonly string _navKey;
    private readonly string _namespace;

    public NamespaceContentRow(
        string label, int count, string navKey, Action<string, string>? open, string ns)
    {
        Label = label;
        Count = count;
        _navKey = navKey;
        _open = open;
        _namespace = ns;

        CountText = count.ToString(CultureInfo.InvariantCulture);

        // Nothing to go to is not a link. A zero that clicks through to an empty list is a promise
        // the page already answered.
        CanOpen = open is not null && count > 0;
    }

    public string Label { get; }
    public int Count { get; }
    public string CountText { get; }
    public bool CanOpen { get; }

    [RelayCommand]
    private void Open() => _open?.Invoke(_navKey, _namespace);
}
