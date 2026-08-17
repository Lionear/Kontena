using System.Collections.ObjectModel;
using System.Text.Json;
using k8s;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Adapters.Kubernetes;

/// <summary>How much of the cluster this Prometheus looks in for rules.</summary>
public enum RuleNamespaceScope
{
    /// <summary>Not known — the CR could not be read, or it says something we will not guess at.</summary>
    Unknown,

    /// <summary>A null <c>ruleNamespaceSelector</c>: the Prometheus' own namespace and nothing else.</summary>
    OwnNamespace,

    /// <summary>An empty <c>ruleNamespaceSelector</c>: every namespace.</summary>
    AllNamespaces,

    /// <summary>Namespaces carrying <see cref="RuleTargeting.NamespaceLabels"/>.</summary>
    ByLabels,
}

/// <summary>
/// What a cluster's Prometheus would and would not pick up, read off its own CR.
/// <para>
/// Two selectors, two different silent failures, and one read answers both.
/// <c>ruleNamespaceSelector</c> decides whether the namespace is looked in at all;
/// <c>ruleSelector</c> is a label selector on the <c>PrometheusRule</c> object itself, and a rule
/// missing that label applies cleanly, is ignored, and says so nowhere. That second one is the most
/// common way a hand-written rule does nothing.
/// </para>
/// <para>
/// Every "we do not know" is carried rather than smoothed over. A field that says <i>watched</i>
/// because it could not read the selector is worse than one that admits it could not read it: the
/// first sends someone away believing the rule will fire.
/// </para>
/// </summary>
public sealed record RuleTargeting
{
    private static readonly IReadOnlyDictionary<string, string> None =
        ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Nothing was read — both questions fall back to saying so.</summary>
    public static RuleTargeting Unread(string refusal) =>
        new() { NamespaceRefusal = refusal, SelectorRefusal = refusal };

    /// <summary>Which namespaces are watched, when that is knowable.</summary>
    public RuleNamespaceScope Scope { get; init; } = RuleNamespaceScope.Unknown;

    /// <summary>Where the Prometheus object itself lives — what <see cref="RuleNamespaceScope.OwnNamespace"/> means.</summary>
    public string PrometheusNamespace { get; init; } = string.Empty;

    /// <summary>The labels a namespace needs under <see cref="RuleNamespaceScope.ByLabels"/>.</summary>
    public IReadOnlyDictionary<string, string> NamespaceLabels { get; init; } = None;

    /// <summary>
    /// <c>metadata.labels</c> the object must carry to be selected — what the editor prefills and
    /// tints, because it is not the author's label to remove.
    /// </summary>
    public IReadOnlyDictionary<string, string> RequiredLabels { get; init; } = None;

    /// <summary>
    /// A null <c>ruleSelector</c>, which selects no <c>PrometheusRule</c> at all. Rare, and worth
    /// its own sentence: no label will help, and the fix is on the Prometheus rather than the rule.
    /// </summary>
    public bool SelectsNothing { get; init; }

    /// <summary>Why watched/not-watched cannot be judged; null when it can.</summary>
    public string? NamespaceRefusal { get; init; }

    /// <summary>Why the required labels are not known; null when they are.</summary>
    public string? SelectorRefusal { get; init; }

    /// <summary>Whether the editor can prefill and defend the selector label.</summary>
    public bool KnowsSelector => SelectorRefusal is null;

    /// <summary>
    /// Whether rules in <paramref name="ns"/> are looked at. <b>Null is a real answer</b> — it means
    /// the selector could not be read, which the field says out loud instead of picking a side.
    /// </summary>
    public bool? Watches(KubeNamespace ns) => Scope switch
    {
        RuleNamespaceScope.AllNamespaces => true,
        RuleNamespaceScope.OwnNamespace => string.Equals(ns.Name, PrometheusNamespace, StringComparison.Ordinal),
        RuleNamespaceScope.ByLabels => NamespaceLabels.All(want =>
            ns.Labels.TryGetValue(want.Key, out var have) && string.Equals(have, want.Value, StringComparison.Ordinal)),
        _ => null,
    };
}

/// <summary>
/// Reads <see cref="RuleTargeting"/> off <c>prometheuses.monitoring.coreos.com</c> — one extra API
/// call, and the only way to answer "will this rule actually be picked up" before it is applied.
/// </summary>
internal static class PrometheusRuleTargetingReader
{
    private const string Group = "monitoring.coreos.com";
    private const string Version = "v1";
    private const string Plural = "prometheuses";

    /// <param name="preferNamespace">
    /// Where discovery found a Prometheus answering. A cluster may run several Prometheus objects,
    /// and the one Kontena is reading alerts from is the one whose selectors describe this page.
    /// </param>
    public static async Task<RuleTargeting> ReadAsync(
        IKubernetes client, string? preferNamespace, CancellationToken ct = default)
    {
        JsonElement items;
        try
        {
            var raw = await client.CustomObjects
                .ListClusterCustomObjectAsync(Group, Version, Plural, cancellationToken: ct)
                .ConfigureAwait(false);

            if (raw is not JsonElement root
                || !root.TryGetProperty("items", out items)
                || items.ValueKind != JsonValueKind.Array)
            {
                return RuleTargeting.Unread(
                    "Kontena could not read this cluster's Prometheus object, so it cannot say which "
                    + "namespaces and labels it selects rules by.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Most often a namespaced user, or a cluster with no Operator at all. Both are ordinary,
            // and both leave the editor working — it just stops claiming to know the answer.
            return RuleTargeting.Unread(
                $"Kontena could not read {Plural}.{Group} on this cluster ({K8sErrors.Map(ex, "cluster").Message}), "
                + "so it cannot say which namespaces and labels this Prometheus selects rules by.");
        }

        var all = items.EnumerateArray().ToList();

        // Prefer the one alerts are already being read from; fall back to the whole list so a cluster
        // whose Prometheus is reachable under an unexpected name still gets an answer.
        var candidates = preferNamespace is { Length: > 0 } prefer
            ? all.Where(p => NamespaceOf(p) == prefer).ToList()
            : all;

        if (candidates.Count == 0)
            candidates = all;

        if (candidates.Count != 1)
        {
            return RuleTargeting.Unread(candidates.Count == 0
                ? "This cluster serves the PrometheusRule CRD but runs no Prometheus object, so nothing "
                  + "here would pick a rule up yet."
                : $"This cluster runs {candidates.Count} Prometheus objects, so Kontena will not guess "
                  + "which one's selectors apply to this rule.");
        }

        return From(candidates[0]);
    }

    /// <summary>
    /// One Prometheus object's two selectors, read the way the Operator reads them. Split out from the
    /// call so the semantics — which are the surprising part — can be tested without a cluster.
    /// </summary>
    internal static RuleTargeting From(JsonElement prometheus)
    {
        var spec = prometheus.TryGetProperty("spec", out var s) ? s : default;

        var namespaces = ReadSelector(spec, "ruleNamespaceSelector");
        var rules = ReadSelector(spec, "ruleSelector");

        return new RuleTargeting
        {
            PrometheusNamespace = NamespaceOf(prometheus),

            // The Operator's own reading of an absent selector, which is not the obvious one: a null
            // ruleNamespaceSelector is the Prometheus' own namespace, and an empty one is all of them.
            Scope = namespaces switch
            {
                { Unsupported: true } => RuleNamespaceScope.Unknown,
                { Present: false } => RuleNamespaceScope.OwnNamespace,
                { MatchLabels.Count: 0 } => RuleNamespaceScope.AllNamespaces,
                _ => RuleNamespaceScope.ByLabels,
            },
            NamespaceLabels = namespaces.MatchLabels,
            NamespaceRefusal = namespaces.Unsupported
                ? "This Prometheus selects namespaces with matchExpressions, which Kontena does not "
                  + "evaluate — so it will not claim either way whether this one is watched."
                : null,

            // And a null ruleSelector is the mirror image: it selects nothing, rather than everything.
            SelectsNothing = !rules.Present,
            RequiredLabels = rules.MatchLabels,
            SelectorRefusal = rules.Unsupported
                ? "This Prometheus selects rules with matchExpressions, so Kontena cannot prefill the "
                  + "label the object needs — check ruleSelector on the Prometheus before applying."
                : null,
        };
    }

    private static string NamespaceOf(JsonElement obj) =>
        obj.TryGetProperty("metadata", out var meta) && meta.TryGetProperty("namespace", out var ns)
            ? ns.GetString() ?? string.Empty
            : string.Empty;

    /// <param name="Present">False when the key is absent or null — which the two selectors read
    /// as opposite things, so it is carried rather than collapsed into an empty match.</param>
    /// <param name="Unsupported">The selector uses <c>matchExpressions</c>; nothing here evaluates them.</param>
    private readonly record struct Selector(
        bool Present, IReadOnlyDictionary<string, string> MatchLabels, bool Unsupported);

    private static Selector ReadSelector(JsonElement spec, string key)
    {
        if (spec.ValueKind != JsonValueKind.Object
            || !spec.TryGetProperty(key, out var selector)
            || selector.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new Selector(Present: false, ReadOnlyDictionary<string, string>.Empty, Unsupported: false);
        }

        if (selector.TryGetProperty("matchExpressions", out var expressions)
            && expressions.ValueKind == JsonValueKind.Array
            && expressions.GetArrayLength() > 0)
        {
            return new Selector(Present: true, ReadOnlyDictionary<string, string>.Empty, Unsupported: true);
        }

        var labels = selector.TryGetProperty("matchLabels", out var match) && match.ValueKind == JsonValueKind.Object
            ? match.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty, StringComparer.Ordinal)
            : [];

        return new Selector(Present: true, labels, Unsupported: false);
    }
}
