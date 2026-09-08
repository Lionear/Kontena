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

/// <summary>
/// Shared shape of the Workload and Service detail pages (KON-166, KON-167).
/// <para>
/// Both tickets ask for the same thing from two sides — identity, the pods that belong to this
/// object, its events, its manifest — and both warn that building them apart is how the two pages
/// end up disagreeing. So the tabs, the events tab, the manifest tab and the related-pods list live
/// here once, and each subclass supplies only what is genuinely different about its kind.
/// </para>
/// <para>
/// Deliberately not shared with <see cref="ClusterPodDetailViewModel"/>: that page streams logs, holds
/// a terminal and has an editable manifest with an apply. Folding it in would mean carrying all of
/// that here for two pages that need none of it.
/// </para>
/// </summary>
public abstract partial class ClusterObjectDetailViewModel : ViewModelBase, IDisposable, IDetachableDetail
{
    private readonly IClusterEngine _cluster;
    private readonly Action<Pod>? _onOpenPod;
    private readonly Action? _onDelete;
    private CancellationTokenSource? _watch;
    private CancellationTokenSource? _usage;

    // No onBack: Back is the shell's history now, and a page that carried its own would be a second
    // way out that has to be kept in step with the first (KON-173).
    /// <param name="onDelete">Invoked by the header's Delete (KON-334). The shell's, not the page's:
    /// deleting what a detail shows also has to close that detail and drop the history step that
    /// leads back to it, and neither is the page's to do. Left null by the kinds this page shape also
    /// serves but that have no business offering a delete — a Node and a Namespace.</param>
    protected ClusterObjectDetailViewModel(
        IClusterEngine cluster, ResourceRef reference, Action<Pod>? onOpenPod, Action? onDelete = null)
    {
        _cluster = cluster;
        _onOpenPod = onOpenPod;
        _onDelete = onDelete;
        Reference = reference;

        if (cluster.Capabilities.Watch)
        {
            _watch = new CancellationTokenSource();
            _ = FollowForGoneAsync(_watch.Token);
        }
    }

    /// <summary>The cluster this page reads from, for a subclass that re-reads its own object.</summary>
    protected IClusterEngine Cluster => _cluster;

    /// <summary>
    /// Whether this object is known to be gone (KON-308) — a Deleted event for it on the same watch
    /// the list pages already follow (KON-250), or that watch ending on its own. A cluster this fake
    /// or the real Kubernetes adapter can't watch never sets this; there is simply no signal.
    /// </summary>
    [ObservableProperty] private bool _isSourceGone;

    private async Task FollowForGoneAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var e in _cluster.WatchAsync(Reference.Kind, Reference.Namespace, ct))
            {
                if (e.Resource.Name != Reference.Name)
                    continue;

                if (e.Type == WatchEventType.Deleted)
                {
                    IsSourceGone = true;
                    return;
                }

                // Everything else about this object was read once, in the constructor, and never
                // again (KON-448): the loop watched only for the object disappearing, so a rollout
                // could run from start to finish with the header still showing the reading from
                // before it began. The pods tab was refreshed by hand after a restart, which is why
                // the staleness was easy to miss — the half of the page that was watched was the
                // half nobody was looking at.
                // Re-read rather than read the event: a ResourceEvent carries a reference and not a
                // manifest on purpose (KON-355), so "something moved" is all it can tell anyone.
                // ponytail: one read per event, uncoalesced. Only this object's events get here, so a
                // rollout is a handful of reads over its lifetime; if that ever shows up, ClusterWatch
                // already settles a burst into one redraw and is the thing to reuse.
                if (e.Type == WatchEventType.Modified)
                    await OnResourceModifiedAsync();
            }

            // The stream ended without being cancelled. An apiserver closes a watch on its own
            // schedule, and there is no more specific answer left than "this can no longer be checked".
            if (!ct.IsCancellationRequested)
                IsSourceGone = true;
        }
        catch (OperationCanceledException)
        {
            // Page closed.
        }
        catch (Exception)
        {
            if (!ct.IsCancellationRequested)
                IsSourceGone = true;
        }
    }

    /// <summary>
    /// The watched object changed (KON-448). Does nothing by default: a page that shows only fields
    /// which cannot change without the object being replaced has nothing to re-read, and a blanket
    /// re-fetch on every kind would put one read per event on pages that never needed it.
    /// </summary>
    protected virtual Task OnResourceModifiedAsync() => Task.CompletedTask;

    /// <summary>Stop following. Cluster pages are rebuilt on every visit, so a watch that outlived its
    /// page would be a stream nobody reads holding a connection open for the life of the app.
    /// Virtual so <see cref="ClusterServiceDetailViewModel"/> can drop its own subscription first
    /// (KON-321).</summary>
    public virtual void Dispose()
    {
        _watch?.Cancel();
        _watch?.Dispose();
        _watch = null;
        _usage?.Cancel();
        _usage?.Dispose();
        _usage = null;
        GC.SuppressFinalize(this);
    }

    // ── Usage graphs (KON-347) ────────────────────────────────────────────────

    /// <summary>
    /// The usage charts, on the kinds that configured them. Null where the page has none — a config
    /// map has no CPU — and the Metrics tab hides itself accordingly.
    /// </summary>
    public UsageTrackViewModel? Usage { get; private set; }

    public bool ShowUsageGraphs => Usage is not null;

    /// <summary>
    /// Give this page usage charts, fed by <paramref name="sample"/> every scrape interval.
    /// <para>
    /// A poll rather than a stream because there is no node or workload equivalent of
    /// <c>StreamMetricsAsync</c>: <see cref="IMetricsSource"/> answers one snapshot at a time, and
    /// adding a stream per kind to <see cref="IClusterEngine"/> would change the interface for every
    /// backend to move a loop that belongs here anyway.
    /// </para>
    /// </summary>
    /// <param name="sample">Returns one value per chart, or null when this tick has no answer.</param>
    /// <param name="caveat">Passed to the track — see UsageTrackViewModel's historyCaveat.</param>
    protected void ConfigureUsage(
        IEnumerable<UsageChartSpec> charts, UsageTarget target,
        Func<CancellationToken, Task<double[]?>> sample, string? caveat = null)
    {
        if (!_cluster.Capabilities.Metrics)
            return;

        Usage = new UsageTrackViewModel(
            charts, target,
            _cluster is IMetricsHistoryAware historyAware ? historyAware.History : null,
            _cluster is IMetricsAware metricsAware ? metricsAware.Metrics.Name : "the metrics source",
            caveat);

        OnPropertyChanged(nameof(ShowUsageGraphs));

        _usage = new CancellationTokenSource();
        _ = Usage.ProbeAsync(_usage.Token);
        _ = PollUsageAsync(sample, _usage.Token);
    }

    private async Task PollUsageAsync(Func<CancellationToken, Task<double[]?>> sample, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (await sample(ct).ConfigureAwait(true) is { } values && Usage is { } usage)
                    usage.Add(DateTimeOffset.UtcNow, values);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // One failed read is a gap, not the end of the page's charts.
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

    protected ResourceRef Reference { get; }

    /// <summary>Kind/name(/namespace) — stable across the list reloads that hand this page a brand
    /// new record for the same object (KON-308). Shared by all four subclasses, like IsSourceGone.</summary>
    public string DetailKey => Reference.ToString();

    /// <summary>Whether the shell wired a delete for this kind (KON-334).</summary>
    public bool CanDelete => _onDelete is not null;

    [RelayCommand]
    private void Delete() => _onDelete?.Invoke();

    // Taken from the reference rather than declared abstract: it is the same fact twice, and a
    // subclass that answered differently from the reference it was constructed with would read its
    // events and manifest for one object while naming another.
    public string Name => Reference.Name;
    public string Namespace => Reference.Namespace ?? string.Empty;

    /// <summary>What the pods tab is called for this kind — "Pods" is not always the honest word.</summary>
    public virtual string PodsTabLabel => "Pods";

    /// <summary>
    /// Whether this kind has a pods tab at all — false on a StorageClass (KON-445), which has no
    /// pods of its own and no label-selector or reference to match one by. Same shape as
    /// <see cref="ShowUsageGraphs"/>: a tab that hides itself rather than opening onto an empty,
    /// unexplained list.
    /// </summary>
    public virtual bool ShowPodsTab => true;

    /// <summary>
    /// Which namespace this page's pods and events are read from; null means every one.
    /// <para>
    /// The object's own namespace is right for a Deployment or a Service and wrong for both of the
    /// cluster-scoped kinds (KON-197): a Node's pods are spread across every namespace there is, and
    /// a Namespace <i>is</i> the scope rather than living in one.
    /// </para>
    /// </summary>
    protected virtual string? Scope => Reference.Namespace is { Length: > 0 } ns ? ns : null;

    // ── Tabs ─────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _selectedTab = "overview";

    partial void OnSelectedTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsOverviewSelected));
        OnPropertyChanged(nameof(IsPodsSelected));
        OnPropertyChanged(nameof(IsEventsSelected));
        OnPropertyChanged(nameof(IsMetricsSelected));
        OnPropertyChanged(nameof(IsYamlSelected));

        if (value == "events" && !_eventsLoaded)
            _ = LoadEventsAsync();
        if (value == "yaml" && !_yamlLoaded)
            _ = LoadYamlAsync();
    }

    public bool IsOverviewSelected => SelectedTab == "overview";
    public bool IsPodsSelected => SelectedTab == "pods";
    public bool IsEventsSelected => SelectedTab == "events";
    public bool IsMetricsSelected => SelectedTab == "metrics";
    public bool IsYamlSelected => SelectedTab == "yaml";

    [RelayCommand]
    private void SelectTab(string tab) => SelectedTab = tab;

    // ── Related pods ──────────────────────────────────────────────────────────

    public ObservableCollection<PodRow> Pods { get; } = [];

    [ObservableProperty] private bool _podsLoading = true;

    /// <summary>
    /// Why the list is empty, when it is. An empty list on its own reads as "nothing is running",
    /// which is only one of the reasons it can be empty — and rarely the one you need to know.
    /// </summary>
    [ObservableProperty] private string? _podsEmptyReason;

    public bool HasPods => Pods.Count > 0;

    /// <summary>Ready pods out of matched pods — the distinction between "none" and "none working".</summary>
    [ObservableProperty] private string _podsSummary = string.Empty;

    /// <summary>
    /// Refresh the pods tab in place (KON-323): a restart or a scale changes what is running under
    /// this object, not the object's own identity, so there is nothing to gain from closing the
    /// drawer and rebuilding it just to show the new pods arrive.
    /// </summary>
    public Task RefreshPodsAsync() => LoadPodsAsync();

    protected async Task LoadPodsAsync()
    {
        PodsLoading = true;
        try
        {
            var all = await _cluster.ListPodsAsync(Scope);
            var mine = SelectPods(all);

            Pods.Clear();
            foreach (var p in mine)
                Pods.Add(new PodRow(p, _onOpenPod));

            PodsEmptyReason = mine.Count > 0 ? null : EmptyPodsReason();

            var ready = mine.Count(p => p.Phase == PodPhase.Running && p.ReadyContainers == p.Containers.Count);
            PodsSummary = mine.Count == 0
                ? string.Empty
                : ready == mine.Count
                    ? $"{mine.Count} pod{(mine.Count == 1 ? "" : "s")}, all ready"
                    // The reading that matters when nothing is arriving: pods exist, none are serving.
                    : $"{ready} of {mine.Count} pod{(mine.Count == 1 ? "" : "s")} ready";
        }
        catch
        {
            PodsEmptyReason = "Could not read the pods in this namespace.";
        }
        finally
        {
            PodsLoading = false;
            OnPropertyChanged(nameof(HasPods));
        }
    }

    /// <summary>Which of the namespace's pods belong to this object.</summary>
    protected abstract IReadOnlyList<Pod> SelectPods(IReadOnlyList<Pod> all);

    /// <summary>What an empty result means for this kind.</summary>
    protected abstract string EmptyPodsReason();

    // ── Events ────────────────────────────────────────────────────────────────

    public ObservableCollection<PodEventRow> Events { get; } = [];
    [ObservableProperty] private bool _eventsLoading;
    private bool _eventsLoaded;

    public bool HasEvents => Events.Count > 0;

    private async Task LoadEventsAsync()
    {
        EventsLoading = true;
        try
        {
            var events = await _cluster.ListEventsAsync(Scope);
            Events.Clear();

            // Matched on kind as well as name: a Deployment and its Service commonly share a name, and
            // showing one's events under the other is worse than showing none.
            foreach (var e in events.Where(e =>
                         e.InvolvedObject.Name == Name
                         && e.InvolvedObject.Kind.Kind == Reference.Kind.Kind))
            {
                Events.Add(new PodEventRow(e));
            }

            _eventsLoaded = true;
        }
        catch
        {
            // leave empty
        }
        finally
        {
            EventsLoading = false;
            OnPropertyChanged(nameof(HasEvents));
        }
    }

    // ── Manifest ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The manifest, editable and appliable (KON-252).
    /// <para>
    /// This tab was read-only on purpose, with a comment saying a text box that silently did nothing
    /// on edit would be worse than none. That was right, and the answer was never to keep it
    /// read-only — it was to build the missing half, which pod detail already had and no other page
    /// could reach.
    /// </para>
    /// <para>
    /// Built on first visit rather than in the constructor: it fetches, and most visits to a detail
    /// page never open this tab.
    /// </para>
    /// </summary>
    [ObservableProperty] private ManifestEditorViewModel? _yaml;

    private bool _yamlLoaded;

    private Task LoadYamlAsync()
    {
        Yaml ??= new ManifestEditorViewModel(_cluster, Reference);
        _yamlLoaded = true;
        return Task.CompletedTask;
    }

    /// <summary>Renders a label map as the "k=v, k=v" chips both pages show.</summary>
    protected static string FormatLabels(IReadOnlyDictionary<string, string> labels) =>
        labels.Count == 0 ? "—" : string.Join(", ", labels.Select(kv => $"{kv.Key}={kv.Value}"));
}

/// <summary>
/// Workload detail (KON-166). The row used to be a dead end: Scale and Restart and nothing else, so
/// the way from a Deployment to its pods was to go to Pods and filter by hand.
/// </summary>
public sealed partial class ClusterWorkloadDetailViewModel : ClusterObjectDetailViewModel
{
    private readonly RestartTracker? _restarts;
    private Workload _workload;

    /// <param name="restarts">Lets the header say "Restarting…" over a status the cluster has not
    /// moved yet (KON-448). Null on the detached detail window and in tests, which then simply show
    /// the cluster's own reading.</param>
    public ClusterWorkloadDetailViewModel(
        IClusterEngine cluster, Workload workload,
        Action<Pod>? onOpenPod = null, Action<Workload>? onScale = null, Action<Workload>? onRestart = null,
        Action? onDelete = null, RestartTracker? restarts = null)
        : base(cluster, workload.Reference, onOpenPod, onDelete)
    {
        _workload = workload;
        _onScale = onScale;
        _onRestart = onRestart;
        _restarts = restarts;

        // Live is the sum over the pods this workload has right now; history traces them through
        // kube_pod_owner, so a rollout's replaced pods still count (KON-347).
        ConfigureUsage(
            [
                new UsageChartSpec("CPU", UsageChartUnit.Millicores, "Primary", UsageMetric.Cpu, "millicores"),
                new UsageChartSpec("Memory", UsageChartUnit.Bytes, "Accent", UsageMetric.Memory, "working set"),
            ],
            UsageTarget.Workload(workload.Namespace, workload.Name, workload.Kind.ToString()),
            async ct =>
            {
                if (cluster is not IMetricsAware aware)
                    return null;

                var usage = await aware.Metrics.GetPodUsageAsync(workload.Namespace, ct).ConfigureAwait(false);

                // Matched against the pods the page already lists rather than by name pattern: the
                // list comes from the apiserver's own ownership chain, which is the answer a regex
                // over pod names is only guessing at.
                var mine = Pods.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
                var rows = usage.Where(u => mine.Contains(u.Pod)).ToList();

                return rows.Count == 0
                    ? null
                    : [rows.Sum(p => (double)p.CpuMillicores), rows.Sum(p => (double)p.MemoryBytes)];
            });

        _ = LoadPodsAsync();
    }

    private readonly Action<Workload>? _onScale;
    private readonly Action<Workload>? _onRestart;

        public string KindText => _workload.Kind.ToString();
    public string ImagesText => _workload.Images.Count == 0 ? "—" : string.Join(", ", _workload.Images);
    public string LabelsText => FormatLabels(_workload.Labels);
    public string SelectorText => FormatLabels(_workload.Selector);
    public string StrategyText => _workload.Strategy.Length == 0 ? "—" : _workload.Strategy;
    public string AgeText => Format.Duration(_workload.Age);

    /// <summary>Whether the restart asked for here has yet to show up in what the cluster reports.</summary>
    private bool IsRestarting => _restarts?.IsRestarting(Reference, DateTimeOffset.UtcNow) == true;

    public string RolloutText => IsRestarting ? RestartTracker.Restarting : _workload.RolloutStatus.ToString();

    /// <summary>A CronJob has a schedule where the others have replicas.</summary>
    public bool IsCronJob => _workload.Kind == WorkloadKind.CronJob;
    public string ScheduleText => _workload.Schedule.Length == 0 ? "—" : _workload.Schedule;

    /// <summary>
    /// The replica breakdown, which is the reason you open this page — one number cannot tell you that
    /// three pods are ready but only two carry the current revision.
    /// </summary>
    public bool ShowReplicas => !IsCronJob;
    public string DesiredText => _workload.Desired.ToString(CultureInfo.InvariantCulture);
    public string ReadyText => _workload.Ready.ToString(CultureInfo.InvariantCulture);
    public string UpToDateText => _workload.UpToDate.ToString(CultureInfo.InvariantCulture);
    public string AvailableText => _workload.Available.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The same reading the grid gives, in the same colours (KON-420). It used to differ on two of the
    /// four: Progressing was info blue and Paused was amber, so the one status this page is opened for
    /// mid-rollout looked like a notice on the row and like a warning here.
    /// </summary>
    public IBrush RolloutBrush => new SolidColorBrush(Color.Parse(
        IsRestarting ? RestartTracker.RestartingColour : _workload.RolloutStatus switch
        {
            RolloutStatus.Complete => "#34D399",
            RolloutStatus.Progressing => "#F5B14C",
            RolloutStatus.Degraded => "#F87171",
            _ => "#5C6675",
        }));

    public bool CanScale => _onScale is not null && _workload.IsScalable;
    public bool CanRestart => _onRestart is not null && !IsCronJob;

    [RelayCommand] private void Scale() => _onScale?.Invoke(_workload);
    [RelayCommand] private void Restart() => _onRestart?.Invoke(_workload);

    /// <summary>
    /// Re-read this workload after the watch said it changed (KON-448), and redraw the fields that
    /// can differ. Everything else in the header is identity — a Deployment does not change
    /// namespace or kind — so only the rollout reading and the replica counts are raised.
    /// <para>
    /// Via <c>ListWorkloadsAsync</c> filtered by kind and namespace because that is the only read
    /// there is: <see cref="IClusterEngine"/> has no get-one-workload, and the list is what fills the
    /// grid this page was opened from anyway. A workload that is not in the answer is one that has
    /// just been deleted, and the same watch is already about to say so.
    /// </para>
    /// </summary>
    protected override Task OnResourceModifiedAsync() => RefreshAsync();

    /// <inheritdoc cref="OnResourceModifiedAsync"/>
    /// <remarks>Also called straight after a restart is requested from this page, so the header
    /// answers the click without waiting for the first watch event.</remarks>
    public async Task RefreshAsync()
    {
        IReadOnlyList<Workload> all;
        try
        {
            all = await Cluster.ListWorkloadsAsync(_workload.Kind, _workload.Namespace);
        }
        catch
        {
            // A read that failed says nothing about the workload; the next event tries again.
            return;
        }

        var fresh = all.FirstOrDefault(w => w.Name == _workload.Name);
        if (fresh is null)
            return;

        _workload = fresh;
        _restarts?.Observe([fresh], DateTimeOffset.UtcNow);

        OnPropertyChanged(nameof(RolloutText));
        OnPropertyChanged(nameof(RolloutBrush));
        OnPropertyChanged(nameof(DesiredText));
        OnPropertyChanged(nameof(ReadyText));
        OnPropertyChanged(nameof(UpToDateText));
        OnPropertyChanged(nameof(AvailableText));

        // The pods are the other half of what a rollout changes, and this page's tab holds them.
        await RefreshPodsAsync();
    }

    public override string PodsTabLabel => IsCronJob ? "Jobs" : "Pods";

    protected override IReadOnlyList<Pod> SelectPods(IReadOnlyList<Pod> all) =>
        PodMatching.OwnedBy(all, _workload);

    protected override string EmptyPodsReason() => IsCronJob
        // Not a gap in the reading: no pod is ever controlled by a CronJob. Saying "no pods" here
        // would describe a healthy CronJob as if something were wrong with it.
        ? "A CronJob does not own pods directly — each run creates a Job, and that Job owns the pods."
        : _workload.Desired == 0
            ? "Scaled to zero, so there are no pods to show."
            : "No pods are running for this workload yet.";
}

/// <summary>
/// Service detail (KON-167). Built alongside the workload one on purpose: both exist to answer
/// "which pods does this reach", and that answer is the same seam.
/// </summary>
public sealed partial class ClusterServiceDetailViewModel : ClusterObjectDetailViewModel
{
    private readonly Service _service;
    private readonly Action<Service>? _onForward;
    private readonly PortForwardRegistry? _portForwards;

    public ClusterServiceDetailViewModel(
        IClusterEngine cluster, Service service,
        Action<Pod>? onOpenPod = null, Action<Service>? onForward = null,
        PortForwardRegistry? portForwards = null, Action? onDelete = null)
        : base(
            cluster, new ResourceRef(GroupVersionKind.Service, service.Namespace, service.Name),
            onOpenPod, onDelete)
    {
        _service = service;
        _onForward = onForward;
        _portForwards = portForwards;

        foreach (var p in service.Ports)
        {
            Ports.Add(new ServicePortRow(p));
        }

        // KON-321: same reasoning as the pod-detail page — the place a forward was started from is
        // where someone looks to stop it, not only the global Port forwards page.
        if (_portForwards is not null)
            _portForwards.Changed += OnPortForwardsChanged;

        _ = LoadPodsAsync();
    }

        public string TypeText => _service.Type.ToString();
    public string ClusterIpText => _service.ClusterIp.Length == 0 ? "—" : _service.ClusterIp;
    public string ExternalIpText => _service.ExternalIp.Length == 0 ? "—" : _service.ExternalIp;
    public string HostnameText => _service.ClusterDnsName;
    public string SelectorText => FormatLabels(_service.Selector);
    public string AgeText => Format.Duration(_service.Age);

    /// <summary>The full port table — the list view shows only what fits in a column.</summary>
    public ObservableCollection<ServicePortRow> Ports { get; } = [];
    public bool HasPorts => Ports.Count > 0;

    public bool CanForward => _onForward is not null && ActiveForward is null;

    [RelayCommand] private void Forward() => _onForward?.Invoke(_service);

    /// <summary>The forward this page started, if it is still running (KON-321).</summary>
    public ActivePortForward? ActiveForward =>
        _portForwards?.Forwards.FirstOrDefault(f => f.Target == Reference && f.IsActive);

    public bool CanStopForward => ActiveForward is not null;

    [RelayCommand]
    private async Task StopForward()
    {
        if (ActiveForward is { } forward && _portForwards is not null)
            await _portForwards.StopAsync(forward);
    }

    private void OnPortForwardsChanged()
    {
        OnPropertyChanged(nameof(ActiveForward));
        OnPropertyChanged(nameof(CanStopForward));
        OnPropertyChanged(nameof(CanForward));
    }

    public override void Dispose()
    {
        if (_portForwards is not null)
            _portForwards.Changed -= OnPortForwardsChanged;

        base.Dispose();
    }

    public override string PodsTabLabel => "Endpoints";

    protected override IReadOnlyList<Pod> SelectPods(IReadOnlyList<Pod> all) =>
        PodMatching.SelectedBy(all, _service);

    protected override string EmptyPodsReason() => _service.Selector.Count == 0
        // A selector-less service is a deliberate shape, not a broken one: its endpoints are managed
        // by hand or it is an ExternalName alias.
        ? "This service has no selector, so its endpoints are not derived from pods."
        : $"No pods in {Namespace} carry the labels {SelectorText}.";
}

/// <summary>One row of a service's port table.</summary>
public sealed class ServicePortRow
{
    public ServicePortRow(ServicePort p)
    {
        Name = p.Name.Length == 0 ? "—" : p.Name;
        Port = p.Port.ToString(CultureInfo.InvariantCulture);
        TargetPort = p.TargetPort.ToString(CultureInfo.InvariantCulture);
        NodePort = p.NodePort?.ToString(CultureInfo.InvariantCulture) ?? "—";
        Protocol = p.Protocol;
    }

    public string Name { get; }
    public string Port { get; }
    public string TargetPort { get; }
    public string NodePort { get; }
    public string Protocol { get; }
}

/// <summary>
/// StorageClass detail (KON-445). The list answers "what would provision here and what happens to
/// the data" in six columns; this is the same six answered in full, plus the YAML and the events a
/// cluster-scoped object still has, none of which had anywhere to live until now.
/// </summary>
public sealed partial class ClusterStorageClassDetailViewModel : ClusterObjectDetailViewModel
{
    private readonly IClusterEngine _cluster;
    private readonly StorageClass _class;
    private readonly Action<string>? _onOpenClaim;

    /// <param name="onOpenClaim">Route to the claim bound to one of this class's volumes — same
    /// route the Volumes list uses for its own CLAIM column.</param>
    public ClusterStorageClassDetailViewModel(
        IClusterEngine cluster, StorageClass c, Action<string>? onOpenClaim = null)
        : base(cluster, new ResourceRef(GroupVersionKind.StorageClass, null, c.Name), onOpenPod: null)
    {
        ArgumentNullException.ThrowIfNull(c);

        _cluster = cluster;
        _class = c;
        _onOpenClaim = onOpenClaim;

        Provisioner = string.IsNullOrEmpty(c.Provisioner) ? "—" : c.Provisioner;
        Reclaim = c.ReclaimPolicy.ToString();
        IsDefault = c.IsDefault;
        Expansion = c.AllowsExpansion ? "Yes" : "No";
        Age = Format.Duration(c.Age);

        Binding = c.BindingMode == VolumeBindingMode.WaitForFirstConsumer
            ? "When a pod needs it"
            : "As soon as a claim exists";
        BindingDetail = c.BindingMode == VolumeBindingMode.WaitForFirstConsumer
            ? "A claim on this class stays Pending until a pod actually mounts it. That is not a fault."
            : "A claim on this class is provisioned straight away.";

        NoProvisioner = string.IsNullOrEmpty(c.Provisioner) || c.Provisioner == "kubernetes.io/no-provisioner";
        NoProvisionerDetail =
            "Nothing provisions volumes for this class, so a claim naming it waits for a volume someone"
            + " creates by hand.";

        _ = LoadVolumesAsync();
    }

    public string Provisioner { get; }
    public string Reclaim { get; }
    public bool IsDefault { get; }
    public string Expansion { get; }
    public string Binding { get; }
    public string BindingDetail { get; }
    public bool NoProvisioner { get; }
    public string NoProvisionerDetail { get; }
    public string Age { get; }

    /// <summary>
    /// The volumes themselves, not a count with a link to them (KON-445) — Rick, on the first cut:
    /// a click-through to see two rows is a click-through too many when there is room for the two
    /// rows right here.
    /// </summary>
    public ObservableCollection<PersistentVolumeRow> Volumes { get; } = [];

    [ObservableProperty] private bool _volumesLoading = true;

    partial void OnVolumesLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyVolumesNote));

    public bool HasVolumes => Volumes.Count > 0;

    /// <summary>Distinct from <see cref="HasVolumes"/> being false: while loading there is no answer
    /// yet, and "no volumes" would be a guess stated as a fact.</summary>
    public bool ShowEmptyVolumesNote => !VolumesLoading && !HasVolumes;

    public string EmptyVolumesNote { get; } = "No volumes use this class.";

    private async Task LoadVolumesAsync()
    {
        VolumesLoading = true;
        try
        {
            var all = await _cluster.ListVolumesAsync();

            Volumes.Clear();
            foreach (var v in all.Where(v => string.Equals(v.StorageClass, _class.Name, StringComparison.Ordinal)))
                Volumes.Add(new PersistentVolumeRow(v, _onOpenClaim));
        }
        catch (Exception)
        {
            // Leave whatever was already showing rather than clearing it — a refresh that failed is
            // not the same fact as "no volumes use this class".
        }
        finally
        {
            VolumesLoading = false;
            OnPropertyChanged(nameof(HasVolumes));
        }
    }

    // No pods to a StorageClass — its identity is the provisioning policy, not anything scheduled.
    // The volumes list above already answers "what does this affect".
    public override bool ShowPodsTab => false;

    protected override IReadOnlyList<Pod> SelectPods(IReadOnlyList<Pod> all) => [];
    protected override string EmptyPodsReason() => string.Empty;
}
