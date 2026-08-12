using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Kontena.App.Controls;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;
using Kontena.Core.Orchestration;

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
    private CancellationTokenSource? _watch;
    private CancellationTokenSource? _usage;
    private bool _started;

    public ClusterOverviewViewModel(IClusterEngine cluster)
    {
        _cluster = cluster;

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

    [ObservableProperty] private int _nodeCount;
    [ObservableProperty] private int _namespaceCount;
    [ObservableProperty] private int _workloadCount;
    [ObservableProperty] private int _podCount;
    [ObservableProperty] private int _serviceCount;

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
    /// Six reads that know nothing of each other, so all six are started before any is awaited
    /// (KON-338). Sequentially they were six round-trips deep, and this page is the first thing a
    /// cluster shows — on a remote one that wait is the whole first impression.
    /// <para>
    /// Internal rather than private so a test can reload it the way a watch event does — the
    /// first-fetch-only rule above is a claim about the second load, and there is no other way in.
    /// </para>
    /// </summary>
    internal async Task LoadAsync()
    {
        var isFirstLoad = !_hasLoaded;
        if (isFirstLoad)
            IsLoading = true;

        try
        {
            await ReadAsync();
            _hasLoaded = true;
        }
        finally
        {
            if (isFirstLoad)
                IsLoading = false;
        }
    }

    private async Task ReadAsync()
    {
        var infoTask = _cluster.GetInfoAsync().AsTask();
        var nodesTask = _cluster.ListNodesAsync().AsTask();
        var namespacesTask = _cluster.ListNamespacesAsync().AsTask();
        var workloadsTask = _cluster.ListWorkloadsAsync().AsTask();
        var podsTask = _cluster.ListPodsAsync().AsTask();
        var servicesTask = _cluster.ListServicesAsync().AsTask();

        await Task.WhenAll(infoTask, nodesTask, namespacesTask, workloadsTask, podsTask, servicesTask);

        var info = infoTask.Result;
        ClusterName = info.DisplayName;
        Version = info.Version;
        Distribution = info is ClusterInfo ci ? ci.Distribution : "cluster";

        var nodes = nodesTask.Result;
        NodeCount = nodes.Count;
        NamespaceCount = namespacesTask.Result.Count;
        WorkloadCount = workloadsTask.Result.Count;
        PodCount = podsTask.Result.Count;
        ServiceCount = servicesTask.Result.Count;

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
                VersionSkewPolicy.Evaluate(info.Version, n.KubeletVersion))),
        ]);
    }
}

/// <summary>A row in the overview's node table.</summary>
public sealed record NodeRow(string Name, string Roles, string Status, string Version, string Cpu, NodeVersionSkew? Skew = null)
{
    /// <summary>A kubelet outside the supported skew window (KON-95) — the version alone doesn't show it.</summary>
    public bool HasVersionWarning => Skew?.IsProblem == true;

    public string VersionWarningDetail => Skew?.Detail ?? string.Empty;
}
