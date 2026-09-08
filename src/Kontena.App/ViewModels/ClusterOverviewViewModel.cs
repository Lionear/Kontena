using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Kontena.App.Controls;
using Kontena.Sdk.Models;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;
using Kontena.Core.Orchestration;
using Kontena.Core.Versioning;

namespace Kontena.App.ViewModels;

/// <summary>
/// The cluster landing page: identity + resource counts + a compact node table, read from an
/// <see cref="IClusterEngine"/>. Where a cluster is summarised; the per-resource browsers it links
/// on to (nodes, pods, workloads, apply/dry-run) are their own pages, built in KON-73.
/// </summary>
// IDisposable arrives through IClusterLivePage; the usage poll is cancelled in the same Dispose
// the watch already had (KON-340 met KON-347).
public partial class ClusterOverviewViewModel : ViewModelBase, IClusterLivePage
{
    private readonly IClusterEngine _cluster;
    private readonly VersionSupportCheck? _versions;
    private CancellationTokenSource? _watch;
    private CancellationTokenSource? _usage;
    private bool _started;

    /// <summary>The one read this page has out, and the token that ends it (KON-413).</summary>
    private readonly PageLoad _load = new();

    public ClusterOverviewViewModel(IClusterEngine cluster, VersionSupportCheck? versions = null)
    {
        _cluster = cluster;
        _versions = versions;

        // The page you land on, and until now the only one that could not answer "is the cluster
        // busy" (KON-347). Summed over pods rather than over nodes, so it agrees with the namespace
        // and workload charts instead of being a fourth number nobody can reconcile.
        if (cluster.Capabilities.Metrics)
        {
            Usage = new UsageTrackViewModel(
                [
                    new UsageChartSpec("CPU", UsageChartUnit.Millicores, "Primary", UsageMetric.Cpu,
                        "millicores across every pod"),
                    new UsageChartSpec("Memory", UsageChartUnit.Bytes, "Accent", UsageMetric.Memory,
                        "working set across every pod"),
                ],
                UsageTarget.Cluster(),
                cluster is IMetricsHistoryAware historyAware ? historyAware.History : null,
                cluster is IMetricsAware metricsAware ? metricsAware.Metrics.Name : "the metrics source");

            _usage = new CancellationTokenSource();
            _ = Usage.ProbeAsync(_usage.Token);
            _ = PollUsageAsync(_usage.Token);
        }

        _ = LoadAsync();
        StartWatching();
    }

    /// <summary>
    /// Every kind this page counts (KON-340). Seven streams for five numbers and a table reads like a
    /// lot, and the alternative — follow Pods and hope everything else moves with them — was
    /// rejected: a Deployment created with zero replicas, a namespace added on an otherwise idle
    /// cluster, a node cordoned, all produce no pod event at all. A heuristic that silently misses a
    /// case is the failure this whole feature exists to prevent, and Kubernetes watches multiplex
    /// over one connection, so the seven are not seven connections.
    /// </summary>
    public IReadOnlyList<GroupVersionKind> WatchedKinds { get; } =
    [
        GroupVersionKind.Node,
        GroupVersionKind.Namespace,
        GroupVersionKind.Deployment,
        GroupVersionKind.StatefulSet,
        GroupVersionKind.DaemonSet,
        GroupVersionKind.Pod,
        GroupVersionKind.Service,
    ];

    /// <inheritdoc/>
    [ObservableProperty] private bool _isLive;

    /// <inheritdoc/>
    [ObservableProperty] private string? _liveNotice;

    /// <inheritdoc/>
    public Action? Changed { get; set; }

    /// <inheritdoc/>
    public void StartWatching()
    {
        if (_started)
            return;

        _started = true;
        _watch = ClusterWatch.Follow(
            _cluster, WatchedKinds, null,
            reload: async () =>
            {
                await LoadAsync();
                Changed?.Invoke();
            },
            onState: (live, notice) =>
            {
                IsLive = live;
                LiveNotice = notice;
            });
    }

    /// <summary>
    /// Stop following. Cluster pages are rebuilt on every visit, so a watch that outlived its page
    /// would be seven streams nobody reads, held for the life of the app.
    /// </summary>
    public void Dispose()
    {
        _watch?.Cancel();
        _watch?.Dispose();
        _watch = null;

        // The usage poll goes the same way and for the same reason (KON-347).
        _usage?.Cancel();
        _usage?.Dispose();
        _usage = null;

        // And the seven reads the page opens with (KON-413). This is the most expensive page in the
        // app to leave running: clicking past the overview used to leave all of them in flight.
        _load.Cancel();

        IsLive = false;
        GC.SuppressFinalize(this);
    }

    /// <summary>Cluster-wide usage, or null where the cluster has no metrics source.</summary>
    public UsageTrackViewModel? Usage { get; }

    public bool ShowUsageGraphs => Usage is not null;

    private async Task PollUsageAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_cluster is IMetricsAware aware)
                {
                    var pods = await aware.Metrics.GetPodUsageAsync(null, ct).ConfigureAwait(true);
                    if (pods.Count > 0 && Usage is { } usage)
                    {
                        usage.Add(
                            DateTimeOffset.UtcNow,
                            pods.Sum(p => (double)p.CpuMillicores),
                            pods.Sum(p => (double)p.MemoryBytes));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // One failed read is a gap, not the end of the charts.
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [ObservableProperty] private string _clusterName = string.Empty;
    [ObservableProperty] private string _distribution = string.Empty;
    [ObservableProperty] private string _version = string.Empty;

    /// <summary>
    /// What the distribution's own calendar says about the version in the header (KON-371). Kept apart
    /// from <see cref="NodeRow.Skew"/> on purpose, even though the two draw the same icon: skew asks
    /// whether the parts of this cluster agree with each other, and is a comparison that is always
    /// right; support asks whether anybody still repairs this release, and is somebody else's
    /// published date. Merging them would make one warning that can only half explain itself.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSupportWarning))]
    [NotifyPropertyChangedFor(nameof(SupportDetail))]
    private VersionSupport? _support;

    /// <summary>Whether this release is one its publisher has dropped.</summary>
    public bool HasSupportWarning => Support?.IsProblem == true;

    /// <summary>The sentence behind that icon — which line, and since when.</summary>
    public string SupportDetail => Support?.Detail ?? string.Empty;

    [ObservableProperty] private int _nodeCount;
    [ObservableProperty] private int _namespaceCount;
    [ObservableProperty] private int _workloadCount;
    [ObservableProperty] private int _podCount;
    [ObservableProperty] private int _serviceCount;

    /// <summary>
    /// The ceiling, not the load: allocatable CPU and memory summed over the Ready nodes (KON-378).
    /// Read off the node objects rather than the metrics source, so both tiles are filled in on a
    /// cluster that has no metrics-server — and NotReady nodes are left out, because capacity nothing
    /// can be scheduled onto is not capacity.
    /// </summary>
    [ObservableProperty] private string _maxCpu = "—";

    /// <inheritdoc cref="MaxCpu"/>
    [ObservableProperty] private string _maxMemory = "—";

    public ObservableCollection<NodeRow> Nodes { get; } = [];

    /// <summary>
    /// Set for the duration of the first read, the same rule the list pages follow (KON-319,
    /// KON-375). This is the page a cluster opens on, and until its six reads land every count on it
    /// is zero and the node table is empty — which is indistinguishable from a cluster that really
    /// has nothing on it. The one page with no rows to be conspicuously absent was the one page with
    /// no sign that anything was happening.
    /// <para>
    /// Only the first read: this page reloads on every settled watch event, and a spinner on each of
    /// those is noise rather than news.
    /// </para>
    /// </summary>
    [ObservableProperty] private bool _isLoading;

    private bool _hasLoaded;

    /// <summary>
    /// Reads that know nothing of each other, so all are started before any is awaited (KON-338).
    /// Sequentially they were six round-trips deep, and this page is the first thing a cluster shows —
    /// on a remote one that wait is the whole first impression.
    /// <para>
    /// Internal rather than private so a test can reload it the way a watch event does — the
    /// first-fetch-only rule above is a claim about the second load, and there is no other way in.
    /// </para>
    /// </summary>
    internal async Task LoadAsync()
    {
        var ct = _load.Begin();

        var isFirstLoad = !_hasLoaded;
        if (isFirstLoad)
            IsLoading = true;

        try
        {
            await Services.Diag.TimeAsync("cluster overview read", ReadAsync(ct));
            _hasLoaded = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Left, or reloaded under this one. What is on screen stays as it was (KON-413).
            return;
        }
        finally
        {
            if (isFirstLoad && !ct.IsCancellationRequested)
                IsLoading = false;
        }

        // Once, not on every watch event: the answer is cached for a day anyway, and this page reloads
        // whenever anything on the cluster moves (KON-375). A cluster does not change version under a
        // page that is already open, and if it does, the page is rebuilt on the next visit.
        if (isFirstLoad)
            await CheckSupportAsync();
    }

    /// <summary>
    /// Ask the distribution's publisher whether this release is still maintained (KON-371). Measured
    /// against the distribution's own calendar rather than upstream's, which is the whole reason KON-95
    /// was split: an AKS cluster read against upstream is called unsupported about a month early.
    /// </summary>
    private async Task CheckSupportAsync()
    {
        if (_versions is null)
            return;

        var product = BackendProducts.For(_cluster.Backend, Distribution);
        Support = await _versions.CheckAsync(product, Version, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Who this cluster is, read once (KON-355). Identity does not change under a page that is
    /// already open — the same claim <see cref="CheckSupportAsync"/> is already made on, three lines
    /// down — and this page re-read it on every watch event, which on a live cluster is every one to
    /// five seconds. <see cref="IClusterEngine.GetInfoAsync"/> is two round-trips in the Kubernetes
    /// adapter, one of them a full node listing, for a name and a version string that were already
    /// on screen.
    /// </summary>
    private BackendInfo? _info;

    /// <summary>
    /// What the workload tile adds up — the same five kinds <c>ListWorkloadsAsync</c> rolls up, minus
    /// the ReplicaSets it treats as an implementation detail of a Deployment. Counted kind by kind
    /// because that is how the API server serves them; it is the same five round-trips one unfiltered
    /// workload list was already making.
    /// </summary>
    private static readonly GroupVersionKind[] WorkloadKinds =
    [
        GroupVersionKind.Deployment,
        GroupVersionKind.StatefulSet,
        GroupVersionKind.DaemonSet,
        GroupVersionKind.Job,
        GroupVersionKind.CronJob,
    ];

    private async Task ReadAsync(CancellationToken ct)
    {
        var infoTask = _info is null ? _cluster.GetInfoAsync(ct).AsTask() : Task.FromResult(_info);

        // The node table is the only thing on this page made of objects rather than of a number, and
        // it shows capacity, version and status — not the pods column the nodes grid has. Asking for
        // the pod counts would fetch every pod on the cluster to fill a field nothing here draws
        // (KON-395).
        var nodesTask = _cluster.ListNodesAsync(withPodCounts: false, ct).AsTask();

        // Four tiles are four integers. They used to be four full listings — every pod, workload,
        // service and namespace on the cluster, deserialised, mapped, and then counted — repeated on
        // every settled watch burst, which on a big cluster never stops arriving.
        var namespacesTask = _cluster.CountAsync(GroupVersionKind.Namespace, ct: ct).AsTask();
        var podsTask = _cluster.CountAsync(GroupVersionKind.Pod, ct: ct).AsTask();
        var servicesTask = _cluster.CountAsync(GroupVersionKind.Service, ct: ct).AsTask();
        var workloadTasks = WorkloadKinds.Select(k => _cluster.CountAsync(k, ct: ct).AsTask()).ToArray();

        await Task.WhenAll([infoTask, nodesTask, namespacesTask, podsTask, servicesTask, .. workloadTasks]);

        // Nothing below this line is a read; it is the page being written. A load that lost its page
        // while the cluster was answering stops here rather than drawing onto it (KON-413).
        ct.ThrowIfCancellationRequested();

        var info = _info = infoTask.Result;
        ClusterName = info.DisplayName;
        Version = info.Version;
        Distribution = info is ClusterInfo ci ? ci.Distribution : "cluster";

        var nodes = nodesTask.Result;
        NodeCount = nodes.Count;

        (MaxCpu, MaxMemory) = Ceiling(nodes);

        NamespaceCount = namespacesTask.Result;
        WorkloadCount = workloadTasks.Sum(t => t.Result);
        PodCount = podsTask.Result;
        ServiceCount = servicesTask.Result;

        // Reconciled rather than cleared and refilled, now that this runs on every watch event and not
        // only once (KON-340). NodeRow is a record, so a node that did not change is the same row and
        // stays in place; clearing would flash the whole table every time any pod on the cluster moved.
        ListSync.Apply(Nodes,
        [
            .. nodes.Select(n => new NodeRow(
                n.Name,
                n.Roles.Count > 0 ? string.Join(", ", n.Roles) : "—",
                n.Status,
                n.KubeletVersion,
                n.Usage is null ? "—" : $"{n.Usage.CpuMillicores}m / {n.Capacity.CpuMillicores}m",
                n.Usage is null ? "—" : $"{Format.Size(n.Usage.MemoryBytes)} / {Format.Size(n.Capacity.MemoryBytes)}",
                VersionSkewPolicy.Evaluate(info.Version, n.KubeletVersion))),
        ]);
    }

    /// <summary>
    /// What the two capacity tiles say (KON-378). Its own method so the Ready rule can be tested
    /// without a cluster: a node that is NotReady still reports its allocatable capacity, and counting
    /// it would put cores in the total that nothing can be scheduled onto.
    /// </summary>
    internal static (string Cpu, string Memory) Ceiling(IEnumerable<Node> nodes)
    {
        var ready = nodes.Where(n => n.Status == "Ready").ToList();
        return (
            Format.Cores(ready.Sum(n => n.Capacity.CpuMillicores)),
            Format.Size(ready.Sum(n => n.Capacity.MemoryBytes)));
    }
}

/// <summary>A row in the overview's node table.</summary>
public sealed record NodeRow(
    string Name, string Roles, string Status, string Version, string Cpu, string Memory,
    NodeVersionSkew? Skew = null)
{
    /// <summary>A kubelet outside the supported skew window (KON-95) — the version alone doesn't show it.</summary>
    public bool HasVersionWarning => Skew?.IsProblem == true;

    public string VersionWarningDetail => Skew?.Detail ?? string.Empty;
}
