using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Sdk.Orchestration;

/// <summary>
/// Where a cluster's alerts come from — the sibling of <see cref="IMetricsSource"/>, and abstract
/// for the same reason: Prometheus and Alertmanager are the common answer, not the only one, and
/// plenty of clusters run neither.
/// <para>
/// It spans two servers on purpose. Alert instances and silences live in Alertmanager; rule state
/// (<see cref="AlertState.Pending"/>, <see cref="AlertState.Inactive"/>, rule health) and expression
/// evaluation live in Prometheus. Splitting the contract along that seam would push "ask the other
/// one" into every caller, when the question a view has is simply "what is wrong right now".
/// </para>
/// <para>
/// What is <b>not</b> here: routing and receivers. Those are a declarative artefact the operator
/// owns, and Kontena reads them at most (KON-215). And installing a monitoring stack is nobody's job
/// here — that hands off to the existing Helm apply flow.
/// </para>
/// </summary>
public interface IAlertSource
{
    /// <summary>Short name for the UI, e.g. "alertmanager", "none".</summary>
    string Name { get; }

    /// <summary>
    /// Alert instances that are firing, plus the ones Prometheus has
    /// <see cref="AlertState.Pending"/> and has not handed over yet. Silenced instances are
    /// included and carry <see cref="Alert.SilencedBy"/> — filtering them out here would decide for
    /// the view what "someone muted this" should look like.
    /// </summary>
    ValueTask<IReadOnlyList<Alert>> ListAlertsAsync(CancellationToken ct = default);

    /// <summary>
    /// Every alerting rule Prometheus has loaded, firing or not. This is what answers "is my rule
    /// even being evaluated" — a question the alert list structurally cannot.
    /// </summary>
    ValueTask<IReadOnlyList<AlertRule>> ListRulesAsync(CancellationToken ct = default);

    /// <summary>Silences known to Alertmanager, expired ones included.</summary>
    ValueTask<IReadOnlyList<Silence>> ListSilencesAsync(CancellationToken ct = default);

    /// <summary>Create a silence; returns its id.</summary>
    ValueTask<string> CreateSilenceAsync(SilenceRequest request, CancellationToken ct = default);

    /// <summary>End a silence now. Alertmanager expires silences, it does not delete them.</summary>
    ValueTask ExpireSilenceAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Evaluate a PromQL expression against the live cluster, so the editor can say what it would
    /// match before a rule exists. See <see cref="ExprCheck"/> for why this is an evaluation and not
    /// a syntax check.
    /// </summary>
    ValueTask<ExprCheck> CheckExprAsync(string promql, CancellationToken ct = default);
}

/// <summary>
/// Implemented by cluster backends that resolve alerts through a pluggable
/// <see cref="IAlertSource"/>. Optional, exactly like <see cref="IMetricsAware"/>:
/// <see cref="Models.ClusterCapabilities.Alerting"/> already tells the UI whether to show the page,
/// and this adds the detail needed to say <i>which</i> source answered.
/// </summary>
public interface IAlertingAware
{
    /// <summary>The source alerts are read from; never null — an absent one is
    /// <see cref="NoAlertSource"/>.</summary>
    IAlertSource Alerts { get; }
}

/// <summary>
/// The null source: a cluster with no Alertmanager. Reads answer empty, and the two writes throw —
/// a silence that quietly did nothing is worse than one that failed, because the operator walks away
/// believing the pager is off.
/// </summary>
public sealed class NoAlertSource : IAlertSource
{
    public static readonly NoAlertSource Instance = new();

    public string Name => "none";

    public ValueTask<IReadOnlyList<Alert>> ListAlertsAsync(CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<Alert>>([]);

    public ValueTask<IReadOnlyList<AlertRule>> ListRulesAsync(CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<AlertRule>>([]);

    public ValueTask<IReadOnlyList<Silence>> ListSilencesAsync(CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<Silence>>([]);

    public ValueTask<string> CreateSilenceAsync(SilenceRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException("This cluster has no Alertmanager, so there is nothing to silence.");

    public ValueTask ExpireSilenceAsync(string id, CancellationToken ct = default) =>
        throw new NotSupportedException("This cluster has no Alertmanager, so there is nothing to expire.");

    public ValueTask<ExprCheck> CheckExprAsync(string promql, CancellationToken ct = default) =>
        ValueTask.FromResult(new ExprCheck { Parsed = false, Error = "No Prometheus is reachable from this cluster." });
}
