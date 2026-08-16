using System.Text.Json;
using Kontena.Sdk.Orchestration.Models;
using Xunit;

namespace Kontena.Adapters.Kubernetes.Tests;

/// <summary>
/// The two selectors on a Prometheus CR (KON-210). Both defaults are the opposite of the obvious
/// guess — an absent <c>ruleNamespaceSelector</c> is <i>this namespace only</i> and an absent
/// <c>ruleSelector</c> is <i>no rule at all</i> — and getting either backwards means the editor
/// cheerfully tells someone a rule will fire when it never will.
/// </summary>
public class PrometheusRuleTargetingTests
{
    private static RuleTargeting Read(string spec) =>
        PrometheusRuleTargetingReader.From(JsonDocument.Parse(
            $$"""{"metadata":{"name":"k8s","namespace":"monitoring"},"spec":{{spec}}}""").RootElement);

    private static KubeNamespace Namespace(string name, params (string Key, string Value)[] labels) =>
        new() { Name = name, Labels = labels.ToDictionary(l => l.Key, l => l.Value, StringComparer.Ordinal) };

    [Fact]
    public void An_absent_namespace_selector_watches_only_the_namespace_Prometheus_runs_in()
    {
        var targeting = Read("""{"ruleSelector":{}}""");

        Assert.Equal(RuleNamespaceScope.OwnNamespace, targeting.Scope);
        Assert.True(targeting.Watches(Namespace("monitoring")));
        Assert.False(targeting.Watches(Namespace("app")));
    }

    [Fact]
    public void An_empty_namespace_selector_watches_every_namespace()
    {
        var targeting = Read("""{"ruleNamespaceSelector":{},"ruleSelector":{}}""");

        Assert.Equal(RuleNamespaceScope.AllNamespaces, targeting.Scope);
        Assert.True(targeting.Watches(Namespace("anything-at-all")));
    }

    [Fact]
    public void Match_labels_are_tested_against_the_namespaces_own_labels()
    {
        var targeting = Read(
            """{"ruleNamespaceSelector":{"matchLabels":{"monitored":"yes"}},"ruleSelector":{}}""");

        Assert.Equal(RuleNamespaceScope.ByLabels, targeting.Scope);
        Assert.True(targeting.Watches(Namespace("app", ("monitored", "yes"), ("team", "payments"))));
        Assert.False(targeting.Watches(Namespace("app", ("monitored", "no"))));
        Assert.False(targeting.Watches(Namespace("app")));
    }

    /// <summary>
    /// Nothing here evaluates matchExpressions, and the honest answer to that is "cannot tell" — not
    /// "watched", which is the answer that sends someone away believing a rule will fire.
    /// </summary>
    [Fact]
    public void A_namespace_selector_Kontena_does_not_evaluate_answers_null_rather_than_guessing()
    {
        var targeting = Read(
            """
            {"ruleNamespaceSelector":{"matchExpressions":[{"key":"env","operator":"In","values":["prod"]}]},
             "ruleSelector":{}}
            """);

        Assert.Equal(RuleNamespaceScope.Unknown, targeting.Scope);
        Assert.Null(targeting.Watches(Namespace("app")));
        Assert.NotNull(targeting.NamespaceRefusal);
    }

    [Fact]
    public void The_rule_selectors_match_labels_are_what_the_editor_prefills()
    {
        var targeting = Read(
            """{"ruleNamespaceSelector":{},"ruleSelector":{"matchLabels":{"release":"kube-prometheus-stack"}}}""");

        Assert.True(targeting.KnowsSelector);
        Assert.False(targeting.SelectsNothing);
        Assert.Equal("kube-prometheus-stack", targeting.RequiredLabels["release"]);
    }

    [Fact]
    public void An_absent_rule_selector_selects_no_PrometheusRule_at_all()
    {
        var targeting = Read("""{"ruleNamespaceSelector":{}}""");

        Assert.True(targeting.SelectsNothing);
        Assert.Empty(targeting.RequiredLabels);
    }

    [Fact]
    public void An_empty_rule_selector_needs_no_label_on_the_object()
    {
        var targeting = Read("""{"ruleNamespaceSelector":{},"ruleSelector":{}}""");

        Assert.False(targeting.SelectsNothing);
        Assert.Empty(targeting.RequiredLabels);
        Assert.True(targeting.KnowsSelector);
    }

    [Fact]
    public void A_rule_selector_Kontena_does_not_evaluate_refuses_to_prefill_rather_than_prefilling_wrong()
    {
        var targeting = Read(
            """
            {"ruleNamespaceSelector":{},
             "ruleSelector":{"matchExpressions":[{"key":"release","operator":"Exists"}]}}
            """);

        Assert.False(targeting.KnowsSelector);
        Assert.Empty(targeting.RequiredLabels);
        Assert.NotNull(targeting.SelectorRefusal);
    }

    [Fact]
    public void An_unread_CR_says_so_on_both_questions_rather_than_answering_either()
    {
        var targeting = RuleTargeting.Unread("nope");

        Assert.Equal(RuleNamespaceScope.Unknown, targeting.Scope);
        Assert.Null(targeting.Watches(Namespace("monitoring")));
        Assert.False(targeting.KnowsSelector);
        Assert.Equal("nope", targeting.NamespaceRefusal);
        Assert.Equal("nope", targeting.SelectorRefusal);
    }
}
