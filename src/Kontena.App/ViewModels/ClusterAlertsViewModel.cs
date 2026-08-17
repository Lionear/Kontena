using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Adapters.Kubernetes;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.ViewModels;

/// <summary>Which question a group answers. Sections, not a status column — see the class remarks.</summary>
public enum AlertSection
{
    /// <summary>Go and look.</summary>
    Firing,

    /// <summary>Not yet, and maybe never.</summary>
    Pending,

    /// <summary>Someone decided.</summary>
    Silenced,
}

/// <summary>
/// The Alerts page (KON-207): what is wrong right now, grouped the way Alertmanager already groups it.
/// <para>
/// One group per <c>alertname</c>, because Alertmanager groups, dedupes and inhibits before we ever
/// see it, and a UI that flattens all that has thrown away work the server did. The split is the
/// Workloads dashboard's (KON-174): <b>the group header carries the sentence</b> — how many, how
/// long, which receiver — and <b>the rows carry the instances</b>. The flat severity-sorted table is
/// in the mockup as the rejected alternative: denser and sortable, and it makes twelve replicas of
/// one broken Deployment look like twelve problems.
/// </para>
/// <para>
/// Firing, Pending and Silenced are <b>sections rather than a status column</b>. They are different
/// questions, and a column would suggest you weigh them against each other. One alertname can appear
/// in two sections at once, which is honest: two of three pods silenced is exactly that.
/// </para>
/// </summary>
public partial class ClusterAlertsViewModel : ClusterListPageViewModel<AlertGroupRow>
{
    private readonly IClusterEngine _cluster;
    private readonly IAlertSource _alerts;
    private readonly Action? _onInstallWithHelm;
    private readonly Action<Alert, AlertRule?, Silence?>? _onOpenDetail;
    private readonly Action? _onNewRule;

    /// <param name="onInstallWithHelm">
    /// Hands off to the existing Helm source on the apply page with the chart filled in. Kontena owns
    /// no install path of its own here: metrics-server was one pinned manifest, while owning
    /// kube-prometheus-stack means owning its upgrades forever (KON-204).
    /// </param>
    /// <param name="onOpenDetail">Opens the alert-detail drawer for one instance (KON-208), with the
    /// rule and silence its group already resolved — a second lookup for what this page just read
    /// would be a second answer to the same question.</param>
    /// <param name="onNewRule">
    /// Opens the rule editor (KON-210). Offered whatever this cluster runs: authoring a rule needs no
    /// Alertmanager and no CRD — only applying it to this cluster does, and the editor says which.</param>
    public ClusterAlertsViewModel(
        IClusterEngine cluster, Action? onInstallWithHelm = null,
        Action<Alert, AlertRule?, Silence?>? onOpenDetail = null, Action? onNewRule = null)
        // No kind to follow: alerts come off Alertmanager over HTTP, not from the apiserver, so there
        // is no watch to open. Said out loud rather than left as a list that silently never moves.
        : base(cluster, kind: null, ns: null,
            unwatchable: "Alertmanager has no watch stream, so this list is read when you open or refresh it.")
    {
        _cluster = cluster;
        _onInstallWithHelm = onInstallWithHelm;
        _onOpenDetail = onOpenDetail;
        _onNewRule = onNewRule;
        _alerts = cluster is IAlertingAware aware ? aware.Alerts : NoAlertSource.Instance;

        // Where the discovery actually looked, verbatim (KON-206). A cluster running an Alertmanager
        // under a name Kontena does not know has to be shown the gap, and a list typed out here would
        // stop being true the first time a candidate is added to the discovery code.
        if (cluster is KubernetesClusterEngine k)
        {
            LookedFor = k.AlertingLookedFor;
            LookedIn = k.AlertingLookedIn;
            Refusal = k.AlertingRefusal;
        }

        _ = LoadAsync();
        StartWatching();
    }

    public override string SearchPlaceholder => "Search alerts…";

    /// <summary>Whether this cluster has an Alertmanager that answered.</summary>
    public bool HasAlerting => _cluster.Capabilities.Alerting;

    /// <summary>Which source answered, for the notice to name.</summary>
    public string SourceName => _alerts.Name;

    /// <inheritdoc cref="ClusterAlertsViewModel"/>
    public IReadOnlyList<string> LookedFor { get; } = [];

    /// <inheritdoc cref="ClusterAlertsViewModel"/>
    public IReadOnlyList<string> LookedIn { get; } = [];

    /// <summary>Set when the search could not finish — a refused listing reads nothing like an absent one.</summary>
    public string? Refusal { get; }

    public bool HasLookedFor => LookedFor.Count > 0;

    public string LookedInText => LookedIn.Count == 0
        ? string.Empty
        : $"Namespaces searched by name: {string.Join(", ", LookedIn)}.";

    /// <summary>Whether the Helm hand-off can be offered: it ends on the apply page, which needs Apply.</summary>
    public bool CanInstallWithHelm => _cluster.Capabilities.Apply && _onInstallWithHelm is not null;

    /// <summary>
    /// Whether a rule could be applied to this cluster. Independent of <see cref="HasAlerting"/>:
    /// without an Operator a rule can still be written to a file, and without an Alertmanager the
    /// editor still works — so the notice says which half is missing rather than "monitoring absent".
    /// </summary>
    public bool CanApplyRules => _cluster.Capabilities.AlertRules;

    public string RulesNotice => CanApplyRules
        ? "The PrometheusRule CRD is installed, so rules written here can be applied to this cluster."
        : "The PrometheusRule CRD is not installed, so rules can be exported to a file but not applied here.";

    // ── The three sections ───────────────────────────────────────────────────

    public ObservableCollection<AlertGroupRow> Firing { get; } = [];
    public ObservableCollection<AlertGroupRow> Pending { get; } = [];
    public ObservableCollection<AlertGroupRow> Silenced { get; } = [];

    public bool HasFiring => Firing.Count > 0;
    public bool HasPending => Pending.Count > 0;
    public bool HasSilenced => Silenced.Count > 0;

    /// <summary>"Firing · 3" — the count is instances, not groups: it is what the badge counts too.</summary>
    public string FiringCaption => $"{Firing.Sum(g => g.Count)}";
    public string PendingCaption => $"{Pending.Sum(g => g.Count)}";
    public string SilencedCaption => $"{Silenced.Sum(g => g.Count)}";

    /// <summary>Nothing wrong, and the cluster did answer — a different sentence from having no source.</summary>
    public bool IsAllClear => HasLoaded && HasAlerting && All.Count == 0;

    /// <summary>
    /// Whether the rule editor can be reached. Not gated on <see cref="CanApplyRules"/>: a rule
    /// authored without the CRD is still a rule, and the editor is where that gets said.
    /// </summary>
    public bool CanWriteRules => _onNewRule is not null;

    [RelayCommand]
    private void InstallWithHelm() => _onInstallWithHelm?.Invoke();

    [RelayCommand]
    private void NewRule() => _onNewRule?.Invoke();

    protected override async Task<IReadOnlyList<AlertGroupRow>> LoadRowsAsync()
    {
        if (!HasAlerting && _alerts is NoAlertSource)
            return [];

        // Three reads, one page load. Rules carry the `for` a pending group counts against, and
        // silences carry who muted it and until when — neither is on the alert itself.
        var alerts = _alerts.ListAlertsAsync().AsTask();
        var rules = _alerts.ListRulesAsync().AsTask();
        var silences = _alerts.ListSilencesAsync().AsTask();
        await Task.WhenAll(alerts, rules, silences);

        return Group(alerts.Result, rules.Result, silences.Result, _onOpenDetail, ExpireSilence);
    }

    /// <summary>
    /// Split by section first, then by alertname within it. That order is what lets one alertname sit
    /// in two sections when only some of its instances are muted.
    /// </summary>
    internal static IReadOnlyList<AlertGroupRow> Group(
        IReadOnlyList<Alert> alerts, IReadOnlyList<AlertRule> rules, IReadOnlyList<Silence> silences,
        Action<Alert, AlertRule?, Silence?>? onOpenDetail = null, Action<AlertGroupRow>? onExpire = null)
    {
        var ruleByName = rules
            .GroupBy(r => r.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var silenceById = silences.ToDictionary(s => s.Id, StringComparer.Ordinal);
        Silence? SilenceOf(Alert a) =>
            a.SilencedBy.Select(silenceById.GetValueOrDefault).FirstOrDefault(s => s is not null);

        return
        [
            .. alerts
                .GroupBy(a => (Section: SectionOf(a), a.Name))
                .Select(g =>
                {
                    var rule = ruleByName.GetValueOrDefault(g.Key.Name);
                    return new AlertGroupRow(
                        g.Key.Section, g.Key.Name, [.. g], rule,
                        g.Select(SilenceOf).FirstOrDefault(s => s is not null),
                        SilenceOf, onOpenDetail, onExpire);
                })
                // Firing before pending before silenced, then longest-running first: the section is
                // the question, and within one question the oldest is the one that has been ignored
                // longest.
                .OrderBy(g => g.Section)
                .ThenByDescending(g => g.OldestAge),
        ];
    }

    /// <summary>
    /// Un-mute a group's silence (KON-208) — the Silenced section's own action, next to Delete on a
    /// workload and Drain on a node. Confirmed like any other write: an "Expire" that quietly did
    /// nothing would leave someone believing an alert is still muted when it is not.
    /// </summary>
    private void ExpireSilence(AlertGroupRow row)
    {
        if (row.Silence is not { } silence)
            return;

        Confirm(
            "Expire silence",
            $"Stop muting \"{row.Name}\" now? Whatever it was hiding starts showing again immediately.",
            "Expire",
            onConfirm: async () =>
            {
                await _alerts.ExpireSilenceAsync(silence.Id);
                await LoadAsync();
            },
            destructive: false);
    }

    private static AlertSection SectionOf(Alert alert) => alert switch
    {
        { IsSilenced: true } => AlertSection.Silenced,
        { State: AlertState.Pending } => AlertSection.Pending,
        _ => AlertSection.Firing,
    };

    protected override bool Matches(AlertGroupRow row, string term) =>
        Contains(row.Name, term)
        || Contains(row.Severity, term)
        // Searching for a pod name should find the group it is in, not nothing — the instance is
        // what a person remembers, the alertname is what the list is keyed by.
        || row.Instances.Any(i => Contains(i.Target, term) || Contains(i.Detail, term));

    protected override void OnFiltered()
    {
        ListSync.Apply(Firing, [.. Items.Where(g => g.Section == AlertSection.Firing)]);
        ListSync.Apply(Pending, [.. Items.Where(g => g.Section == AlertSection.Pending)]);
        ListSync.Apply(Silenced, [.. Items.Where(g => g.Section == AlertSection.Silenced)]);

        OnPropertyChanged(nameof(HasFiring));
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(HasSilenced));
        OnPropertyChanged(nameof(FiringCaption));
        OnPropertyChanged(nameof(PendingCaption));
        OnPropertyChanged(nameof(SilencedCaption));
        OnPropertyChanged(nameof(IsAllClear));
    }

    /// <summary>
    /// What the sidebar badge counts: firing instances that nobody has muted. Pending is excluded
    /// because it may never fire, and silenced because someone already decided about it — a badge
    /// counting either would make the number mean "things", not "things to look at".
    /// </summary>
    public static int BadgeCount(IReadOnlyList<Alert> alerts) =>
        alerts.Count(a => a.State == AlertState.Firing && !a.IsSilenced);
}

/// <summary>One alertname within one section: the header carries the sentence.</summary>
public sealed partial class AlertGroupRow
{
    private readonly Action<AlertGroupRow>? _onExpire;

    internal AlertGroupRow(
        AlertSection section, string name, IReadOnlyList<Alert> alerts, AlertRule? rule, Silence? silence,
        Func<Alert, Silence?> silenceOf, Action<Alert, AlertRule?, Silence?>? onOpenDetail,
        Action<AlertGroupRow>? onExpire)
    {
        Section = section;
        Name = name;
        Count = alerts.Count;
        Severity = alerts[0].Severity ?? "none";
        Rule = rule;
        Silence = silence;
        _onExpire = onExpire;
        Instances =
        [
            .. alerts
                .Select(a => new AlertInstanceRow(a, opened => onOpenDetail?.Invoke(opened, rule, silenceOf(opened))))
                .OrderByDescending(i => i.Age),
        ];

        var now = DateTimeOffset.UtcNow;
        OldestAge = alerts.Max(a => now - a.StartsAt);

        // Receivers are only claimed where every instance agrees. Alertmanager may match several
        // routes, and naming one of them would be a guess about where the page went.
        var receivers = alerts.SelectMany(a => a.Receivers).Distinct(StringComparer.Ordinal).ToList();
        Receiver = receivers.Count == 1 ? receivers[0] : null;

        var ruleFor = rule?.For;
        Why = section switch
        {
            AlertSection.Pending when ruleFor is { } f =>
                $"true for {Format.Duration(OldestAge)} of for: {Format.Duration(f)} — Prometheus has it, Alertmanager has not been told",
            AlertSection.Pending => $"true for {Format.Duration(OldestAge)}, still inside its for:",
            AlertSection.Silenced when silence is not null =>
                $"by {Or(silence.CreatedBy, "someone")} until {silence.EndsAt.ToLocalTime():HH:mm}"
                + (silence.Comment.Length > 0 ? $" — “{silence.Comment}”" : " — no reason given"),
            AlertSection.Silenced => "muted, and the silence behind it is gone",
            _ => $"firing {Format.Duration(OldestAge)} · {Count} {(Count == 1 ? "instance" : "instances")}",
        };
    }

    private static string Or(string value, string fallback) => value.Length > 0 ? value : fallback;

    public AlertSection Section { get; }
    public string Name { get; }
    public string Severity { get; }
    public int Count { get; }
    public string CountText => Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string Why { get; }
    public string? Receiver { get; }
    public bool HasReceiver => Receiver is not null;
    public TimeSpan OldestAge { get; }
    public IReadOnlyList<AlertInstanceRow> Instances { get; }

    /// <summary>The rule this alertname is evaluated by, when Prometheus reports one.</summary>
    public AlertRule? Rule { get; }

    /// <summary>The silence behind this group's Why, when it is still live — what Expire acts on.</summary>
    public Silence? Silence { get; }

    /// <summary>Only the Silenced section offers it, and only while there is still a live silence to
    /// end — the "silence behind it is gone" case has nothing left to expire.</summary>
    public bool CanExpire => Section == AlertSection.Silenced && Silence is not null && _onExpire is not null;

    [RelayCommand]
    private void Expire() => _onExpire?.Invoke(this);

    /// <summary>
    /// Colour is never the only carrier: <see cref="SectionWord"/> says the same thing in words, so
    /// this is the second signal and not the signal (KON-204).
    /// </summary>
    public string SeverityBrushKey => Section switch
    {
        AlertSection.Pending => "Info",
        AlertSection.Silenced => "TextFaint",
        _ when string.Equals(Severity, "critical", StringComparison.OrdinalIgnoreCase) => "Danger",
        _ => "Warn",
    };

    public string SectionWord => Section switch
    {
        AlertSection.Pending => "pending",
        AlertSection.Silenced => "silenced",
        _ => Severity,
    };

    public string IconKey => Section switch
    {
        AlertSection.Pending => "IconClock",
        AlertSection.Silenced => "IconBellOff",
        _ => "IconWarning",
    };
}

/// <summary>One instance under a group: which pod, which node, since when.</summary>
public sealed partial class AlertInstanceRow
{
    private readonly Alert _alert;
    private readonly Action<Alert>? _onOpen;

    internal AlertInstanceRow(Alert alert, Action<Alert>? onOpen)
    {
        _alert = alert;
        _onOpen = onOpen;

        // The most specific label the alert carries, because that is the object a person goes to
        // look at. Order matters: a pod alert also carries a namespace.
        Target = First(alert, "pod", "node", "instance", "host", "job", "service", "namespace") ?? alert.Name;

        // Everything else worth reading, minus the two the header already says.
        Detail = string.Join(" · ", alert.Labels
            .Where(l => l.Key is not ("alertname" or "severity") && l.Value != Target)
            .OrderBy(l => l.Key, StringComparer.Ordinal)
            .Select(l => $"{l.Key}={l.Value}"));

        Summary = alert.Annotations.GetValueOrDefault("summary")
            ?? alert.Annotations.GetValueOrDefault("description")
            ?? string.Empty;

        Age = DateTimeOffset.UtcNow - alert.StartsAt;
        AgeText = Format.Duration(Age);
        IsSilenced = alert.IsSilenced;
    }

    private static string? First(Alert alert, params string[] keys) =>
        keys.Select(k => alert.Labels.GetValueOrDefault(k)).FirstOrDefault(v => !string.IsNullOrEmpty(v));

    public string Target { get; }
    public string Detail { get; }
    public string Summary { get; }
    public bool HasSummary => Summary.Length > 0;
    public TimeSpan Age { get; }
    public string AgeText { get; }
    public bool IsSilenced { get; }

    /// <summary>Whether the shell wired the alert-detail drawer (KON-208).</summary>
    public bool CanOpen => _onOpen is not null;

    [RelayCommand]
    private void Open() => _onOpen?.Invoke(_alert);
}
