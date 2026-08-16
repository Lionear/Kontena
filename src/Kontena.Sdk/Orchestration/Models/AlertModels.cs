using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace Kontena.Sdk.Orchestration.Models;

/// <summary>
/// Where an alert stands. Deliberately three values and not four: <b>silenced is not a state</b>,
/// it is a decision someone took about an alert that is still firing — see
/// <see cref="Alert.SilencedBy"/>. Modelling it as a fourth state would lose the difference between
/// "this stopped" and "we stopped looking".
/// </summary>
public enum AlertState
{
    /// <summary>The expression has been true for longer than the rule's <c>for</c>: go and look.</summary>
    Firing,

    /// <summary>True now, but not yet for long enough. Not yet, and maybe never.</summary>
    Pending,

    /// <summary>The rule is loaded and evaluating, and its expression is false.</summary>
    Inactive,
}

/// <summary>
/// One alert instance, as Alertmanager reports it (<c>/api/v2/alerts</c>) — or as Prometheus reports
/// it while still <see cref="AlertState.Pending"/>, which Alertmanager has not been told about yet.
/// <para>
/// <b><see cref="Labels"/> is the model, not a bag of extras.</b> An alert has no fixed schema:
/// which labels exist and what they mean is decided by the rule that wrote them and the routing
/// config that reads them. So <c>namespace</c>, <c>pod</c> and <c>node</c> are looked up, not typed —
/// and <see cref="Severity"/> is a convenience accessor over the same dictionary rather than an
/// enum, because <c>critical</c> means whatever the operator's routing says it means.
/// </para>
/// </summary>
public sealed record Alert
{
    /// <summary>Every label on the instance, <c>alertname</c> and <c>severity</c> included.</summary>
    public required IReadOnlyDictionary<string, string> Labels { get; init; }

    /// <summary>Rendered annotations — typically <c>summary</c>, <c>description</c>, <c>runbook_url</c>.</summary>
    public IReadOnlyDictionary<string, string> Annotations { get; init; } =
        ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Firing, or pending when it came off Prometheus' rule state.</summary>
    public AlertState State { get; init; } = AlertState.Firing;

    /// <summary>When this instance started — what "firing for 6h 12m" is measured from.</summary>
    public DateTimeOffset StartsAt { get; init; }

    /// <summary>When it is due to resolve; null while it keeps being re-sent.</summary>
    public DateTimeOffset? EndsAt { get; init; }

    /// <summary>
    /// Receivers this instance routed to. Empty is an honest answer and not an error: a pending
    /// alert has not been routed, and Alertmanager may match more than one route.
    /// </summary>
    public IReadOnlyList<string> Receivers { get; init; } = [];

    /// <summary>Ids of the silences suppressing this instance; empty when nobody muted it.</summary>
    public IReadOnlyList<string> SilencedBy { get; init; } = [];

    /// <summary>
    /// Fingerprints of the alerts inhibiting this one. Alertmanager suppresses an alert when a
    /// bigger one is already firing (node down inhibits every pod on it); showing it anyway would
    /// undo the deduplication the server did.
    /// </summary>
    public IReadOnlyList<string> InhibitedBy { get; init; } = [];

    /// <summary>Alertmanager's own identity for the label set — stable across re-sends.</summary>
    public string Fingerprint { get; init; } = string.Empty;

    /// <summary>
    /// The Prometheus graph Alertmanager recorded when this alert fired — its own answer to "graph
    /// in Prometheus", so nothing here has to rebuild the query from the rule's expression. Null for
    /// a pending instance, which came off Prometheus' rule state rather than Alertmanager.
    /// </summary>
    public string? GeneratorURL { get; init; }

    /// <summary>The <c>alertname</c> label: what the list groups by.</summary>
    public string Name => Labels.GetValueOrDefault("alertname", string.Empty);

    /// <summary>The <c>severity</c> label, or null when the rule set none. Free text on purpose.</summary>
    public string? Severity => Labels.GetValueOrDefault("severity");

    /// <summary>Whether a silence covers this instance. It is still firing underneath.</summary>
    public bool IsSilenced => SilencedBy.Count > 0;
}

/// <summary>
/// An alerting rule as Prometheus has it loaded (<c>/api/v1/rules</c>) — the answer to "is my rule
/// even being evaluated", which the alert list alone cannot give: a rule that never fires and a rule
/// that was never picked up look identical from Alertmanager's side.
/// </summary>
public sealed record AlertRule
{
    /// <summary>The <c>alert:</c> field — becomes the <c>alertname</c> label.</summary>
    public required string Name { get; init; }

    /// <summary>The PromQL expression, verbatim.</summary>
    public required string Expr { get; init; }

    /// <summary>The rule group it sits in.</summary>
    public string Group { get; init; } = string.Empty;

    /// <summary>Namespace of the <c>PrometheusRule</c> it came from, where that is knowable.</summary>
    public string? Namespace { get; init; }

    /// <summary>How long the expression must hold before firing; null means fire immediately.</summary>
    public TimeSpan? For { get; init; }

    /// <summary>Labels the rule adds to every instance it produces.</summary>
    public IReadOnlyDictionary<string, string> Labels { get; init; } =
        ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Annotation templates, unrendered.</summary>
    public IReadOnlyDictionary<string, string> Annotations { get; init; } =
        ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Current evaluation state.</summary>
    public AlertState State { get; init; } = AlertState.Inactive;

    /// <summary>Prometheus' rule health: <c>ok</c>, <c>err</c> or <c>unknown</c>.</summary>
    public string Health { get; init; } = "unknown";

    /// <summary>
    /// Why the last evaluation failed, in Prometheus' own words. The one place a silently broken
    /// rule says so — an inactive rule and a rule that throws every evaluation both fire nothing.
    /// </summary>
    public string? LastError { get; init; }

    /// <summary>The <c>severity</c> label the rule sets, if any. Same reasoning as <see cref="Alert.Severity"/>.</summary>
    public string? Severity => Labels.GetValueOrDefault("severity");
}

/// <summary>
/// One condition on a silence. Mirrors Alertmanager's matcher rather than a plain label dictionary,
/// because a silence routinely mutes by pattern (<c>pod=~"redis-.*"</c>) or by exclusion.
/// </summary>
public sealed record SilenceMatcher
{
    /// <summary>Label name to test.</summary>
    public required string Name { get; init; }

    /// <summary>Literal value, or the pattern when <see cref="IsRegex"/>.</summary>
    public required string Value { get; init; }

    /// <summary>Whether <see cref="Value"/> is a regular expression.</summary>
    public bool IsRegex { get; init; }

    /// <summary>False inverts the match (<c>!=</c> / <c>!~</c>).</summary>
    public bool IsEqual { get; init; } = true;

    /// <summary>
    /// Whether <paramref name="value"/> satisfies this matcher. Regex matchers are <b>fully
    /// anchored</b>, as Alertmanager anchors them — an unanchored <c>pod=~"redis"</c> would mute
    /// every pod with "redis" anywhere in its name, which is the difference between silencing one
    /// workload and silencing a cluster.
    /// </summary>
    public bool Matches(string value)
    {
        var hit = IsRegex
            ? Regex.IsMatch(value, $"^(?:{Value})$", RegexOptions.None, TimeSpan.FromSeconds(1))
            : string.Equals(value, Value, StringComparison.Ordinal);
        return hit == IsEqual;
    }
}

/// <summary>Whether a silence is muting anything right now.</summary>
public enum SilenceStatus
{
    /// <summary>Muting now.</summary>
    Active,

    /// <summary>Scheduled, not started yet.</summary>
    Pending,

    /// <summary>Over — it ran out or somebody expired it.</summary>
    Expired,
}

/// <summary>
/// A silence as Alertmanager holds it. Imperative and time-boxed by design: a silence is never a
/// declarative artefact and never goes into a repository — it expires, and it belongs to whoever
/// set it. Committed, it becomes a permanent mute nobody remembers adding.
/// </summary>
public sealed record Silence
{
    /// <summary>Alertmanager's id — what <c>ExpireSilenceAsync</c> takes.</summary>
    public required string Id { get; init; }

    /// <summary>Conditions an alert must satisfy to be muted. All of them, ANDed.</summary>
    public required IReadOnlyList<SilenceMatcher> Matchers { get; init; }

    /// <summary>When the silence takes effect.</summary>
    public DateTimeOffset StartsAt { get; init; }

    /// <summary>When it lapses. Never absent: an open-ended silence is a rule you deleted quietly.</summary>
    public DateTimeOffset EndsAt { get; init; }

    /// <summary>Who set it, as Alertmanager recorded it.</summary>
    public string CreatedBy { get; init; } = string.Empty;

    /// <summary>Why. The half of a silence that is worth anything three weeks later.</summary>
    public string Comment { get; init; } = string.Empty;

    /// <summary>
    /// Alertmanager's own verdict, not one derived from the local clock. Two machines disagreeing
    /// about the time is exactly how a mute looks active in the UI and is not.
    /// </summary>
    public SilenceStatus Status { get; init; } = SilenceStatus.Active;
}

/// <summary>What to silence, and until when. The write-side counterpart of <see cref="Silence"/>.</summary>
public sealed record SilenceRequest
{
    /// <summary>Conditions to mute on — normally pre-filled from the alert the user opened.</summary>
    public required IReadOnlyList<SilenceMatcher> Matchers { get; init; }

    /// <summary>When it should start; normally now.</summary>
    public DateTimeOffset StartsAt { get; init; }

    /// <summary>When it must stop. Required, for the reason on <see cref="Silence.EndsAt"/>.</summary>
    public DateTimeOffset EndsAt { get; init; }

    /// <summary>Who is asking.</summary>
    public string CreatedBy { get; init; } = string.Empty;

    /// <summary>Why.</summary>
    public string Comment { get; init; } = string.Empty;
}

/// <summary>One series an expression evaluated to, with the value it currently has.</summary>
/// <param name="Labels">The series' label set.</param>
/// <param name="Value">Its value at evaluation time.</param>
public sealed record ExprSample(IReadOnlyDictionary<string, string> Labels, double Value);

/// <summary>
/// What Prometheus says about an expression, asked before a rule is written.
/// <para>
/// This is deliberately an evaluation and not a lint. <c>promtool check rules</c> confirms the
/// syntax, and a misspelled label name is <i>always</i> syntactically correct — its only symptom is
/// a rule that never fires. Evaluating against the live cluster is what surfaces that, and
/// <see cref="MatchesNothing"/> is the warning worth having.
/// </para>
/// </summary>
public sealed record ExprCheck
{
    /// <summary>Whether Prometheus accepted and evaluated the expression.</summary>
    public bool Parsed { get; init; }

    /// <summary>Prometheus' own error text when it did not. Passed through, not reworded.</summary>
    public string? Error { get; init; }

    /// <summary>The series it evaluated to, so the editor can preview what would fire now.</summary>
    public IReadOnlyList<ExprSample> Samples { get; init; } = [];

    /// <summary>Parsed cleanly and matched nothing — valid, and almost certainly not what was meant.</summary>
    public bool MatchesNothing => Parsed && Samples.Count == 0;
}

/// <summary>
/// A rule being written, before it is a <c>PrometheusRule</c>. Neutral on purpose: the same object
/// feeds the cluster apply and the files written for a GitOps repo, which is what keeps applied and
/// exported byte-identical.
/// </summary>
public sealed record AuthoredRule
{
    /// <summary>The <c>alert:</c> name, and so the <c>alertname</c> the list will group by.</summary>
    public required string Name { get; init; }

    /// <summary>The PromQL expression.</summary>
    public required string Expr { get; init; }

    /// <summary>How long it must hold before firing.</summary>
    public TimeSpan? For { get; init; }

    /// <summary>Labels on the alert — <c>severity</c> among them, as a label like any other.</summary>
    public IReadOnlyDictionary<string, string> Labels { get; init; } =
        ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Annotation templates: <c>summary</c>, <c>runbook_url</c>, and so on.</summary>
    public IReadOnlyDictionary<string, string> Annotations { get; init; } =
        ReadOnlyDictionary<string, string>.Empty;

    /// <summary><c>metadata.name</c> of the <c>PrometheusRule</c> that will carry it.</summary>
    public required string ObjectName { get; init; }

    /// <summary><c>metadata.namespace</c>. Free text: a namespace may exist only after the file lands.</summary>
    public required string Namespace { get; init; }

    /// <summary><c>spec.groups[].name</c>. Defaults to <see cref="ObjectName"/> when left empty.</summary>
    public string GroupName { get; init; } = string.Empty;

    /// <summary>
    /// <c>metadata.labels</c> on the <c>PrometheusRule</c> itself — distinct from
    /// <see cref="Labels"/>, which land on the alert. This is what Prometheus' <c>ruleSelector</c>
    /// tests, and the single most common way a hand-written rule silently does nothing: the object
    /// applies cleanly, Prometheus ignores it, and nothing anywhere says why.
    /// </summary>
    public IReadOnlyDictionary<string, string> ObjectLabels { get; init; } =
        ReadOnlyDictionary<string, string>.Empty;
}
