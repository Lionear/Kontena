using Kontena.Adapters.Kubernetes;
using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;
using Xunit;

namespace Kontena.App.Tests;

/// <summary>
/// The rule editor (KON-210). Two claims carry the ticket and both are asserted here rather than
/// read off the code: the manifest that reaches the apply route is the one the panel showed, and the
/// namespace field answers "will this be picked up" instead of only "did you spell it".
/// </summary>
public sealed class RuleEditorViewModelTests
{
    private static RuleTargeting Watching(params (string Key, string Value)[] required) => new()
    {
        Scope = RuleNamespaceScope.ByLabels,
        PrometheusNamespace = "monitoring",

        // The fake's namespaces carry no labels, so an impossible requirement is what makes every
        // one of them "not watched" — the amber case, which is the one worth covering.
        NamespaceLabels = new Dictionary<string, string> { ["monitored"] = "yes" },
        RequiredLabels = required.ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal),
    };

    private static async Task<RuleEditorViewModel> Vm(
        RuleTargeting? targeting = null, Action<ManifestBundle>? onApply = null,
        FakeClusterEngine? cluster = null)
    {
        var vm = new RuleEditorViewModel(
            cluster ?? new FakeClusterEngine(), onApply ?? (_ => { }),
            () => Task.FromResult(targeting ?? new RuleTargeting
            {
                Scope = RuleNamespaceScope.AllNamespaces,
                PrometheusNamespace = "monitoring",
                RequiredLabels = new Dictionary<string, string> { ["release"] = "kube-prometheus-stack" },
            }));

        await vm.Loaded;
        return vm;
    }

    private static void Fill(RuleEditorViewModel vm)
    {
        vm.AlertName = "AppHighErrorRate";
        vm.Expression = "up == 0";
        vm.ObjectName = "checkout-slo";
        vm.NamespaceName = "monitoring";
    }

    /// <summary>
    /// The ticket's central constraint: the editor composes and hands off, it does not apply. What
    /// arrives at the apply route is byte-for-byte what the preview showed.
    /// </summary>
    [Fact]
    public async Task Apply_hands_the_previewed_manifest_to_the_ordinary_apply_route()
    {
        ManifestBundle? handed = null;
        var vm = await Vm(onApply: b => handed = b);
        Fill(vm);

        vm.ApplyCommand.Execute(null);

        Assert.NotNull(handed);
        Assert.Equal(vm.Manifest, handed.Yaml);
        Assert.Equal("monitoring", handed.Namespace);

        // Not a dry-run flag set here: the apply page owns dry-run-then-diff-then-apply, and a bundle
        // that arrived pre-decided would be the second apply path this ticket exists to avoid.
        Assert.False(handed.DryRun);
    }

    [Fact]
    public async Task Nothing_is_applied_until_the_rule_is_complete()
    {
        var vm = await Vm();
        Assert.False(vm.CanApply);
        Assert.NotNull(vm.Incomplete);

        Fill(vm);
        Assert.True(vm.CanApply);
        Assert.Null(vm.Incomplete);
    }

    /// <summary>
    /// Authoring works everywhere; only applying needs the CRD. The editor is reachable either way and
    /// says which half is missing — a page that hid itself would leave the file half unreachable too.
    /// </summary>
    [Fact]
    public async Task Without_the_CRD_the_editor_still_composes_but_cannot_apply()
    {
        var vm = await Vm(cluster: new FakeClusterEngine { HasPrometheusRuleCrd = false });
        Fill(vm);

        Assert.False(vm.CanApplyToCluster);
        Assert.False(vm.CanApply);
        Assert.Contains("PrometheusRule CRD is not installed", vm.ApplyNotice, StringComparison.Ordinal);
        Assert.Contains("kind: PrometheusRule", vm.Manifest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Severity_is_written_as_a_label_and_nothing_more()
    {
        var vm = await Vm();
        Fill(vm);

        Assert.Equal("warning", vm.Severity);
        Assert.Contains("severity: warning", vm.Manifest, StringComparison.Ordinal);

        vm.Severities.First(s => s.Name == "critical").ChooseCommand.Execute(null);

        Assert.Equal("critical", vm.Severity);
        Assert.Equal("critical", vm.Rule.Labels["severity"]);
        Assert.Contains("severity: critical", vm.Manifest, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>ruleSelector</c> tests the object's own labels, and dropping the one it wants is the most
    /// common way a hand-written rule silently does nothing — so the row has no remove.
    /// </summary>
    [Fact]
    public async Task The_selector_label_is_prefilled_on_the_object_and_is_not_the_authors_to_remove()
    {
        var vm = await Vm();
        Fill(vm);

        var row = Assert.Single(vm.ObjectLabels);
        Assert.Equal("release", row.Key);
        Assert.Equal("kube-prometheus-stack", row.Value);
        Assert.False(row.CanRemove);

        Assert.Equal("kube-prometheus-stack", vm.Rule.ObjectLabels["release"]);
        Assert.DoesNotContain("release", vm.Rule.Labels.Keys, StringComparer.Ordinal);
        Assert.True(vm.SelectorNoticeIsWarning);
    }

    [Fact]
    public async Task Emptying_the_selector_label_says_the_object_would_not_be_selected()
    {
        var vm = await Vm();
        vm.ObjectLabels[0].Value = string.Empty;

        Assert.Contains("will not select the object", vm.SelectorNotice, StringComparison.Ordinal);
        Assert.True(vm.SelectorNoticeIsWarning);
    }

    [Fact]
    public async Task A_Prometheus_whose_selectors_could_not_be_read_says_so_rather_than_prefilling()
    {
        var vm = await Vm(RuleTargeting.Unread("Kontena could not read the Prometheus object."));
        vm.NamespaceName = "monitoring";

        Assert.Empty(vm.ObjectLabels);
        Assert.Equal("Kontena could not read the Prometheus object.", vm.SelectorNotice);
        Assert.Equal("Kontena could not read the Prometheus object.", vm.NamespaceVerdict);
        Assert.True(vm.NamespaceVerdictIsWarning);
    }

    [Fact]
    public async Task A_watched_namespace_says_the_rule_will_be_picked_up()
    {
        var vm = await Vm();
        vm.NamespaceName = "app";

        Assert.Contains("Prometheus watches app", vm.NamespaceVerdict, StringComparison.Ordinal);
        Assert.False(vm.NamespaceVerdictIsWarning);
        Assert.Equal("TextDim", vm.NamespaceVerdictBrushKey);
    }

    /// <summary>
    /// The quieter of the two failures, and so the more dangerous: an unwatched namespace applies
    /// cleanly and is then ignored, where a missing namespace at least fails loudly.
    /// </summary>
    [Fact]
    public async Task An_unwatched_namespace_is_amber_and_says_it_would_be_ignored()
    {
        var vm = await Vm(Watching(("release", "kps")));
        vm.NamespaceName = "app";

        Assert.Contains("does not watch app", vm.NamespaceVerdict, StringComparison.Ordinal);
        Assert.Contains("would apply cleanly and then be ignored", vm.NamespaceVerdict, StringComparison.Ordinal);
        Assert.True(vm.NamespaceVerdictIsWarning);
        Assert.Equal("Warn", vm.NamespaceVerdictBrushKey);
    }

    [Fact]
    public async Task A_namespace_that_does_not_exist_is_still_allowed_and_still_composed()
    {
        var vm = await Vm();
        Fill(vm);
        vm.NamespaceName = "not-created-yet";

        Assert.Contains("does not exist on this cluster", vm.NamespaceVerdict, StringComparison.Ordinal);
        Assert.True(vm.NamespaceVerdictIsWarning);

        // Free text is the point: a namespace may exist only after the file lands, and refusing it
        // would break authoring on a cluster Kontena cannot reach.
        Assert.True(vm.CanApply);
        Assert.Contains("namespace: not-created-yet", vm.Manifest, StringComparison.Ordinal);
    }

    /// <summary>
    /// Until the first keystroke the value is a selection, not a query. Without this rule, opening a
    /// filled-in field looks like a dropdown with exactly one item in it.
    /// </summary>
    [Fact]
    public async Task Focusing_a_filled_field_shows_the_whole_list_and_typing_filters_it()
    {
        var vm = await Vm();
        vm.NamespaceName = "monitoring";

        vm.OpenNamespaceMenuCommand.Execute(null);
        Assert.True(vm.IsNamespaceMenuOpen);
        Assert.Equal(vm.NamespaceOptions.Count, vm.NamespaceMatches.Count);
        Assert.True(vm.NamespaceMatches.Count > 1);

        vm.NamespaceName = "mon";
        vm.NamespaceTyped();
        Assert.Equal("monitoring", Assert.Single(vm.NamespaceMatches).Name);

        vm.PickNamespaceCommand.Execute(vm.NamespaceMatches[0]);
        Assert.Equal("monitoring", vm.NamespaceName);
        Assert.False(vm.IsNamespaceMenuOpen);
    }

    [Fact]
    public async Task Every_namespace_carries_the_verdict_the_field_exists_to_give()
    {
        var vm = await Vm(Watching());

        Assert.All(vm.NamespaceOptions, o => Assert.False(o.Watched));
        Assert.All(vm.NamespaceOptions, o => Assert.Equal("Warn", o.WatchedBrushKey));
        Assert.All(vm.NamespaceOptions, o => Assert.Equal("not matched by ruleNamespaceSelector", o.Note));
    }

    [Fact]
    public async Task A_for_that_Prometheus_would_refuse_blocks_the_apply_and_names_the_grammar()
    {
        var vm = await Vm();
        Fill(vm);
        vm.ForText = "10 minutes";

        Assert.False(vm.CanApply);
        Assert.Contains("not a Prometheus duration", vm.Incomplete, StringComparison.Ordinal);

        vm.ForText = "1h30m";
        Assert.True(vm.CanApply);
        Assert.Contains("for: 1h30m", vm.Manifest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_for_leaves_the_key_out_rather_than_writing_a_zero()
    {
        var vm = await Vm();
        Fill(vm);
        vm.ForText = string.Empty;

        Assert.True(vm.CanApply);
        Assert.DoesNotContain("for:", vm.Manifest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Extra_labels_and_annotations_land_on_the_alert_and_empty_keys_are_dropped()
    {
        var vm = await Vm();
        Fill(vm);

        vm.AddLabelCommand.Execute(null);
        vm.Labels[0].Key = "team";
        vm.Labels[0].Value = "payments";

        vm.AddAnnotationCommand.Execute(null);
        vm.Annotations[0].Key = "summary";
        vm.Annotations[0].Value = "Checkout is unhappy";

        // A half-typed row is not a label yet, and writing "": "" would be a manifest the form never
        // showed anyone.
        vm.AddLabelCommand.Execute(null);

        Assert.Contains("team: payments", vm.Manifest, StringComparison.Ordinal);
        Assert.Contains("summary: Checkout is unhappy", vm.Manifest, StringComparison.Ordinal);
        Assert.Equal(2, vm.Rule.Labels.Count);

        vm.Labels[0].RemoveCommand.Execute(null);
        Assert.DoesNotContain("team: payments", vm.Manifest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_expression_field_feeds_the_PromQL_check()
    {
        var vm = await Vm();
        vm.Expression = "up == 0";

        Assert.Equal("up == 0", vm.Check.Expression);
    }
}
