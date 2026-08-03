using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;
using Kontena.Core.Models;
using Kontena.Core.Orchestration;

namespace Kontena.App.ViewModels;

/// <summary>
/// The Workloads landing page (KON-174). Since KON-169 the nav item is a group header, so clicking it
/// opens the group rather than loading every kind into one list — which left the page beside it with
/// nothing to show. This is what goes there.
/// <para>
/// It earns its place over the nav item by never showing a bare number: the nav already carries
/// "Deployments 3", and 3 says nothing about whether they are running. Every count here sits next to
/// its health split or next to a reason.
/// </para>
/// </summary>
public partial class ClusterWorkloadsDashboardViewModel : ViewModelBase, IListPage
{
    private readonly IClusterEngine _cluster;
    private readonly string? _namespace;
    private readonly Action<WorkloadKind>? _onOpenKind;
    private readonly Action<Workload>? _onOpenWorkload;
    private readonly Action? _onOpenPods;

    public ClusterWorkloadsDashboardViewModel(
        IClusterEngine cluster, string? @namespace,
        Action<WorkloadKind>? onOpenKind = null, Action<Workload>? onOpenWorkload = null,
        Action? onOpenPods = null)
    {
        _cluster = cluster;
        _namespace = @namespace;
        _onOpenKind = onOpenKind;
        _onOpenWorkload = onOpenWorkload;
        _onOpenPods = onOpenPods;

        _ = LoadAsync();
    }

    public ObservableCollection<KindCard> Kinds { get; } = [];
    public ObservableCollection<AttentionRow> Attention { get; } = [];

    public bool HasLoaded { get; private set; }

    /// <summary>
    /// Carried to satisfy <see cref="IListPage"/> so Refresh works; never read. The page is cards, not
    /// a list, which is exactly what <see cref="SupportsSearch"/> says out loud so the box is disabled
    /// rather than silently ignoring what you type (KON-164).
    /// </summary>
    public string SearchText { get; set; } = string.Empty;

    public bool SupportsSearch => false;

    // ── Banner ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether anything is wrong. When nothing is, there is deliberately <i>no</i> banner at all —
    /// not a green one. A bar that says "all good" is a line you learn to skip, and once you skip it
    /// you skip the red one too.
    /// </summary>
    [ObservableProperty] private bool _hasTrouble;

    [ObservableProperty] private string _troubleHeadline = string.Empty;
    [ObservableProperty] private string _troubleDetail = string.Empty;

    // ── Loading ───────────────────────────────────────────────────────────────

    public async Task LoadAsync()
    {
        // Both lists once. The cards are a rollup of the workloads and the reasons come from the pods,
        // so fetching them separately per section is two sources for one screen — and two chances to
        // disagree with each other. Side by side rather than one after the other (KON-338).
        var workloadsTask = _cluster.ListWorkloadsAsync(null, _namespace).AsTask();
        var podsTask = _cluster.ListPodsAsync(_namespace).AsTask();
        await Task.WhenAll(workloadsTask, podsTask);
        var workloads = workloadsTask.Result;
        var pods = podsTask.Result;

        Kinds.Clear();
        foreach (var group in WorkloadNavGroups.For(workloads))
        {
            Kinds.Add(new KindCard(group.Kind, workloads.Where(w => w.Kind == group.Kind).ToList(), _onOpenKind));

            // Pods directly after Deployments, the same rule the sidebar follows (KON-342), so the two
            // views tell one story about where Pods belongs.
            if (group.Kind == WorkloadKind.Deployment)
                Kinds.Add(new KindCard(pods, _onOpenPods));
        }

        // No Deployments here, so nothing to sit under and it goes last — the sidebar's answer too.
        if (!workloads.Any(w => w.Kind == WorkloadKind.Deployment))
            Kinds.Add(new KindCard(pods, _onOpenPods));

        _hasWorkloads = workloads.Count > 0;

        Attention.Clear();
        foreach (var w in workloads.Where(WorkloadTrouble.NeedsAttention))
            Attention.Add(new AttentionRow(w, WorkloadTrouble.Describe(w, PodMatching.OwnedBy(pods, w)), _onOpenWorkload));

        HasTrouble = Attention.Count > 0;

        // The headline counts; the rows below name. Saying "redis and migrate" here as well would be
        // the same fact twice, which is what made the two mockup variants collide.
        TroubleHeadline = Attention.Count == 1
            ? $"1 of {workloads.Count} workloads is not running as intended"
            : $"{Attention.Count} of {workloads.Count} workloads are not running as intended";

        var oldest = Attention.Count == 0 ? TimeSpan.Zero : Attention.Max(a => a.Age);
        TroubleDetail = oldest >= TimeSpan.FromMinutes(1)
            ? $"The longest has been this way for {Format.Duration(oldest)}. The rest are at their desired count."
            : "The rest are at their desired count.";

        HasLoaded = true;
        OnPropertyChanged(nameof(HasAttention));
        OnPropertyChanged(nameof(IsEmpty));
    }

    public bool HasAttention => Attention.Count > 0;

    /// <summary>
    /// No workloads at all — a different thing from "none are healthy", and since KON-341 also a
    /// different thing from "no cards": the Pods card is drawn whether or not anything owns them.
    /// </summary>
    public bool IsEmpty => !_hasWorkloads;

    private bool _hasWorkloads;
}

/// <summary>
/// One kind's card: how many, and how they are doing. The card is the way into that kind's page —
/// the same destination the sidebar's sub-entry has, so the dashboard is the submenu drawn as
/// content rather than a second place things live.
/// </summary>
public sealed partial class KindCard : ObservableObject
{
    private readonly Action? _onOpen;

    public KindCard(WorkloadKind kind, IReadOnlyList<Workload> workloads, Action<WorkloadKind>? onOpen)
    {
        _onOpen = () => onOpen?.Invoke(kind);

        Label = WorkloadNavGroups.LabelFor(kind);
        Count = workloads.Count.ToString(CultureInfo.InvariantCulture);
        Unit = workloads.Count == 1
            ? kind == WorkloadKind.CronJob ? "schedule" : "workload"
            : kind == WorkloadKind.CronJob ? "schedules" : "workloads";

        var complete = workloads.Count(w => w.RolloutStatus == RolloutStatus.Complete);
        var progressing = workloads.Count(w => w.RolloutStatus == RolloutStatus.Progressing);
        var degraded = workloads.Count(w => w.RolloutStatus == RolloutStatus.Degraded);
        var paused = workloads.Count(w => w.RolloutStatus == RolloutStatus.Paused);

        var total = Math.Max(1, workloads.Count);
        CompleteWidth = new GridLength(complete / (double)total, GridUnitType.Star);
        ProgressingWidth = new GridLength(progressing / (double)total, GridUnitType.Star);
        DegradedWidth = new GridLength(degraded / (double)total, GridUnitType.Star);
        PausedWidth = new GridLength(paused / (double)total, GridUnitType.Star);

        // Only the states that occur get a legend entry. Listing every state with a zero beside it
        // turns four facts into one wall, and the point of the split is that it reads at a glance.
        var parts = new List<LegendEntry>();
        if (complete > 0)
            parts.Add(new LegendEntry("#34D399", $"{complete} {Healthy(kind)}"));
        if (progressing > 0)
            parts.Add(new LegendEntry("#5AB8FF", $"{progressing} progressing"));
        if (degraded > 0)
            parts.Add(new LegendEntry("#F87171", $"{degraded} {(kind == WorkloadKind.Job ? "failing" : "degraded")}"));
        if (paused > 0)
            parts.Add(new LegendEntry("#F5B14C", $"{paused} {(kind == WorkloadKind.CronJob ? "suspended" : "paused")}"));

        Legend = parts;
    }

    /// <summary>
    /// The Pods card (KON-341). Pods are not a workload kind — they are what a workload produces — but
    /// they sit in this section of the sidebar, and a page that says "pick a kind" while leaving out
    /// one of the entries in its own section is a promise it does not keep.
    /// <para>
    /// The split is the pod phase rather than a rollout state, because a pod has none. Same four
    /// slots and the same colours the Pods page already gives each phase, so a pod that is amber in
    /// the list is amber in this bar too. <see cref="PodPhase.Unknown"/> rides with Failed: the
    /// question this card answers is "is something wrong", and a pod whose state the cluster cannot
    /// report is not a reassurance.
    /// </para>
    /// </summary>
    public KindCard(IReadOnlyList<Pod> pods, Action? onOpen)
    {
        _onOpen = onOpen;

        Label = "Pods";
        Count = pods.Count.ToString(CultureInfo.InvariantCulture);
        Unit = pods.Count == 1 ? "pod" : "pods";

        var running = pods.Count(p => p.Phase == PodPhase.Running);
        var succeeded = pods.Count(p => p.Phase == PodPhase.Succeeded);
        var failed = pods.Count(p => p.Phase is PodPhase.Failed or PodPhase.Unknown);
        var pending = pods.Count(p => p.Phase == PodPhase.Pending);

        var scale = Math.Max(1, pods.Count);
        CompleteWidth = new GridLength(running / (double)scale, GridUnitType.Star);
        ProgressingWidth = new GridLength(succeeded / (double)scale, GridUnitType.Star);
        DegradedWidth = new GridLength(failed / (double)scale, GridUnitType.Star);
        PausedWidth = new GridLength(pending / (double)scale, GridUnitType.Star);

        var entries = new List<LegendEntry>();
        if (running > 0)
            entries.Add(new LegendEntry("#34D399", $"{running} running"));
        if (succeeded > 0)
            entries.Add(new LegendEntry("#5AB8FF", $"{succeeded} completed"));
        if (failed > 0)
            entries.Add(new LegendEntry("#F87171", $"{failed} failed"));
        if (pending > 0)
            entries.Add(new LegendEntry("#F5B14C", $"{pending} pending"));

        Legend = entries;
    }

    /// <summary>
    /// What "healthy" is called for this kind. A CronJob that is not suspended has not <i>completed</i>
    /// anything — it is waiting for its next run, and saying "complete" claims a run that never
    /// happened. A Job, by contrast, really did finish.
    /// </summary>
    private static string Healthy(WorkloadKind kind) => kind switch
    {
        WorkloadKind.CronJob => "scheduled",
        WorkloadKind.Job => "completed",
        _ => "complete",
    };

    public string Label { get; }
    public string Count { get; }
    public string Unit { get; }
    public IReadOnlyList<LegendEntry> Legend { get; }

    public GridLength CompleteWidth { get; }
    public GridLength ProgressingWidth { get; }
    public GridLength DegradedWidth { get; }
    public GridLength PausedWidth { get; }

    [RelayCommand]
    private void Open() => _onOpen?.Invoke();
}

/// <summary>One segment of a kind card's health bar, with the word that goes with the colour.</summary>
public sealed class LegendEntry
{
    public LegendEntry(string colour, string text)
    {
        Brush = new SolidColorBrush(Color.Parse(colour));
        Text = text;
    }

    public IBrush Brush { get; }
    public string Text { get; }
}

/// <summary>A workload that is not running as intended, and why.</summary>
public sealed partial class AttentionRow : ObservableObject
{
    private readonly Workload _workload;
    private readonly Action<Workload>? _onOpen;

    public AttentionRow(Workload workload, string why, Action<Workload>? onOpen)
    {
        _workload = workload;
        _onOpen = onOpen;

        Name = workload.Name;
        Kind = workload.Kind.ToString();
        Ready = workload.Kind == WorkloadKind.CronJob ? "—" : $"{workload.Ready}/{workload.Desired}";
        Why = why;
        Age = workload.Age;
        AgeText = Format.Duration(workload.Age);
    }

    public string Name { get; }
    public string Kind { get; }
    public string Ready { get; }
    public string Why { get; }
    public TimeSpan Age { get; }
    public string AgeText { get; }

    [RelayCommand]
    private void Open() => _onOpen?.Invoke(_workload);
}
