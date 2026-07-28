using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Orchestration;
using Kontena.Core.Orchestration.Models;

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
public abstract partial class ClusterObjectDetailViewModel : ViewModelBase
{
    private readonly IClusterEngine _cluster;
    private readonly Action<Pod>? _onOpenPod;

    // No onBack: Back is the shell's history now, and a page that carried its own would be a second
    // way out that has to be kept in step with the first (KON-173).
    protected ClusterObjectDetailViewModel(
        IClusterEngine cluster, ResourceRef reference, Action<Pod>? onOpenPod)
    {
        _cluster = cluster;
        _onOpenPod = onOpenPod;
        Reference = reference;
    }

    protected ResourceRef Reference { get; }

    // Taken from the reference rather than declared abstract: it is the same fact twice, and a
    // subclass that answered differently from the reference it was constructed with would read its
    // events and manifest for one object while naming another.
    public string Name => Reference.Name;
    public string Namespace => Reference.Namespace ?? string.Empty;

    /// <summary>What the pods tab is called for this kind — "Pods" is not always the honest word.</summary>
    public virtual string PodsTabLabel => "Pods";

    // ── Tabs ─────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _selectedTab = "overview";

    partial void OnSelectedTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsOverviewSelected));
        OnPropertyChanged(nameof(IsPodsSelected));
        OnPropertyChanged(nameof(IsEventsSelected));
        OnPropertyChanged(nameof(IsYamlSelected));

        if (value == "events" && !_eventsLoaded)
            _ = LoadEventsAsync();
        if (value == "yaml" && !_yamlLoaded)
            _ = LoadYamlAsync();
    }

    public bool IsOverviewSelected => SelectedTab == "overview";
    public bool IsPodsSelected => SelectedTab == "pods";
    public bool IsEventsSelected => SelectedTab == "events";
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

    protected async Task LoadPodsAsync()
    {
        PodsLoading = true;
        try
        {
            var all = await _cluster.ListPodsAsync(Namespace);
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
            var events = await _cluster.ListEventsAsync(Namespace);
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

    [ObservableProperty] private string _yamlText = string.Empty;
    [ObservableProperty] private bool _yamlLoading;
    private bool _yamlLoaded;

    private async Task LoadYamlAsync()
    {
        YamlLoading = true;
        try
        {
            YamlText = await _cluster.GetManifestAsync(Reference);
            _yamlLoaded = true;
        }
        catch
        {
            YamlText = "# Could not fetch the manifest.";
        }
        finally
        {
            YamlLoading = false;
        }
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
    private readonly Workload _workload;

    public ClusterWorkloadDetailViewModel(
        IClusterEngine cluster, Workload workload,
        Action<Pod>? onOpenPod = null, Action<Workload>? onScale = null, Action<Workload>? onRestart = null)
        : base(cluster, workload.Reference, onOpenPod)
    {
        _workload = workload;
        _onScale = onScale;
        _onRestart = onRestart;

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
    public string RolloutText => _workload.RolloutStatus.ToString();

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

    public IBrush RolloutBrush => new SolidColorBrush(Color.Parse(_workload.RolloutStatus switch
    {
        RolloutStatus.Complete => "#34D399",
        RolloutStatus.Progressing => "#5AB8FF",
        RolloutStatus.Degraded => "#F87171",
        _ => "#F5B14C",
    }));

    public bool CanScale => _onScale is not null && _workload.IsScalable;
    public bool CanRestart => _onRestart is not null && !IsCronJob;

    [RelayCommand] private void Scale() => _onScale?.Invoke(_workload);
    [RelayCommand] private void Restart() => _onRestart?.Invoke(_workload);

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

    public ClusterServiceDetailViewModel(
        IClusterEngine cluster, Service service,
        Action<Pod>? onOpenPod = null, Action<Service>? onForward = null)
        : base(cluster, new ResourceRef(GroupVersionKind.Service, service.Namespace, service.Name), onOpenPod)
    {
        _service = service;
        _onForward = onForward;

        foreach (var p in service.Ports)
        {
            Ports.Add(new ServicePortRow(p));
        }

        _ = LoadPodsAsync();
    }

        public string TypeText => _service.Type.ToString();
    public string ClusterIpText => _service.ClusterIp.Length == 0 ? "—" : _service.ClusterIp;
    public string ExternalIpText => _service.ExternalIp.Length == 0 ? "—" : _service.ExternalIp;
    public string SelectorText => FormatLabels(_service.Selector);
    public string AgeText => Format.Duration(_service.Age);

    /// <summary>The full port table — the list view shows only what fits in a column.</summary>
    public ObservableCollection<ServicePortRow> Ports { get; } = [];
    public bool HasPorts => Ports.Count > 0;

    public bool CanForward => _onForward is not null;

    [RelayCommand] private void Forward() => _onForward?.Invoke(_service);

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
