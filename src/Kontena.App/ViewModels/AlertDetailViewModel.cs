using CommunityToolkit.Mvvm.Input;
using Kontena.App.Services;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.ViewModels;

/// <summary>
/// The alert-detail drawer (KON-208): same shape as <see cref="ClusterPodDetailViewModel"/>, holding
/// one alert instance.
/// <para>
/// The footer is where this differs from a wall of key-values. An alert carries <c>namespace</c>,
/// <c>pod</c> — labels Kontena already has a page for — so the footer is a set of jumps to them
/// rather than a repeat of what the labels already say. Reading the same alert in Slack gets you the
/// labels; this is the difference a tool makes.
/// </para>
/// <para>
/// Silence lives here and only here (KON-204 §5): it is imperative, never a manifest, and it always
/// carries an expiry — <see cref="DefaultSilenceDuration"/> is filled in rather than left blank,
/// because an open-ended silence is a rule deleted without saying so. Creating and expiring both
/// bubble to the shell as an intent, the same way <c>ClusterPodDetailViewModel.Delete</c> does,
/// because the confirmation — what exactly gets muted, until when — is the shell's to show.
/// </para>
/// </summary>
public partial class AlertDetailViewModel : ViewModelBase
{
    /// <summary>How long a silence lasts when nobody says otherwise — long enough to outlast an
    /// incident response, short enough that a forgotten one does not quietly become permanent.</summary>
    public static readonly TimeSpan DefaultSilenceDuration = TimeSpan.FromHours(2);

    private readonly Alert _alert;
    private readonly AlertRule? _rule;
    private readonly Silence? _silence;
    private readonly Func<ResourceRef, string, Task<bool>>? _onOpenPod;
    private readonly Action<SilenceRequest>? _onSilence;
    private readonly Action<Silence>? _onExpireSilence;

    public AlertDetailViewModel(
        Alert alert, AlertRule? rule, Silence? silence,
        Func<ResourceRef, string, Task<bool>>? onOpenPod = null,
        Action<SilenceRequest>? onSilence = null, Action<Silence>? onExpireSilence = null)
    {
        _alert = alert;
        _rule = rule;
        _silence = silence;
        _onOpenPod = onOpenPod;
        _onSilence = onSilence;
        _onExpireSilence = onExpireSilence;
    }

    // ── Header ───────────────────────────────────────────────────────────────

    public string Name => _alert.Name;
    public string Severity => _alert.Severity ?? "none";

    public string StateWord => _alert.State switch
    {
        AlertState.Firing => "firing",
        AlertState.Pending => "pending",
        _ => "inactive",
    };

    public string StartedText =>
        $"{_alert.StartsAt.ToLocalTime():HH:mm} · {Format.Duration(DateTimeOffset.UtcNow - _alert.StartsAt)} ago";

    public string RoutedToText => _alert.Receivers.Count > 0 ? string.Join(", ", _alert.Receivers) : "not routed yet";

    // ── Rule ─────────────────────────────────────────────────────────────────

    // No PrometheusRule object to link to: Prometheus reports the rule file it loaded from, not the
    // PrometheusRule it was rendered from (AlertRule.Namespace stays null), and reversing the
    // operator's naming scheme would be a guess a jump button should never make.
    public bool HasRule => _rule is not null;
    public string RuleGroup => _rule?.Group ?? string.Empty;
    public string RuleHealth => _rule?.Health ?? "unknown";
    public string RuleExpr => _rule?.Expr ?? string.Empty;

    // ── Labels ───────────────────────────────────────────────────────────────

    public IReadOnlyList<LabelChip> Labels =>
        [.. _alert.Labels.OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => new LabelChip(l.Key, l.Value))];

    // ── Silence ──────────────────────────────────────────────────────────────

    public bool IsSilenced => _silence is not null;

    public string SilenceStatusText => _silence is { } s
        ? $"Silenced by {(s.CreatedBy.Length > 0 ? s.CreatedBy : "someone")} until {s.EndsAt.ToLocalTime():HH:mm}"
          + (s.Comment.Length > 0 ? $" — “{s.Comment}”" : string.Empty)
        : $"Expires by default in {Format.Duration(DefaultSilenceDuration)} — a silence without an end is an alert you deleted.";

    public bool CanCreateSilence => !IsSilenced && _onSilence is not null;
    public bool CanExpireSilence => IsSilenced && _onExpireSilence is not null;

    [RelayCommand]
    private void Silence() => _onSilence?.Invoke(BuildSilenceRequest());

    [RelayCommand]
    private void ExpireSilence()
    {
        if (_silence is { } s)
            _onExpireSilence?.Invoke(s);
    }

    private SilenceRequest BuildSilenceRequest()
    {
        var now = DateTimeOffset.UtcNow;
        return new SilenceRequest
        {
            Matchers = MatchersFor(_alert),
            StartsAt = now,
            EndsAt = now.Add(DefaultSilenceDuration),
            CreatedBy = Environment.UserName,
        };
    }

    /// <summary>
    /// Every label but <c>severity</c> — routing metadata, not identity — so the silence covers
    /// exactly this alert's label set and nothing looser. Mirrors the seeded example in
    /// <c>FakeAlertSource</c>.
    /// </summary>
    internal static IReadOnlyList<SilenceMatcher> MatchersFor(Alert alert) =>
        [.. alert.Labels
            .Where(l => l.Key != "severity")
            .OrderBy(l => l.Key, StringComparer.Ordinal)
            .Select(l => new SilenceMatcher { Name = l.Key, Value = l.Value })];

    // ── Footer jumps ─────────────────────────────────────────────────────────

    private string? RunbookUrl => _alert.Annotations.GetValueOrDefault("runbook_url");
    public bool CanOpenRunbook => RunbookUrl is not null;

    [RelayCommand]
    private void OpenRunbook()
    {
        if (RunbookUrl is { } url)
            Browser.OpenUrl(url);
    }

    private ResourceRef? PodRef =>
        _alert.Labels.TryGetValue("namespace", out var ns) && _alert.Labels.TryGetValue("pod", out var pod)
            ? new ResourceRef(GroupVersionKind.Pod, ns, pod)
            : null;

    public bool CanOpenPod => PodRef is not null && _onOpenPod is not null;
    public bool CanOpenLogs => CanOpenPod;
    public string PodButtonLabel => PodRef is { } r ? $"Pod {r.Name}" : "Pod";

    [RelayCommand]
    private async Task OpenPod()
    {
        if (PodRef is { } target && _onOpenPod is not null)
            await _onOpenPod(target, "overview");
    }

    [RelayCommand]
    private async Task OpenLogs()
    {
        if (PodRef is { } target && _onOpenPod is not null)
            await _onOpenPod(target, "logs");
    }

    // Alertmanager's own answer to "graph in Prometheus" — the query it recorded when the alert
    // fired, so nothing here rebuilds it from the rule's expression.
    public bool CanOpenGraph => _alert.GeneratorURL is not null;

    [RelayCommand]
    private void OpenGraph()
    {
        if (_alert.GeneratorURL is { } url)
            Browser.OpenUrl(url);
    }
}

/// <summary>One label chip in the alert-detail body.</summary>
public sealed record LabelChip(string Key, string Value)
{
    public string Text => $"{Key}={Value}";
}
