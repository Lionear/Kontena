using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// The plan's quiet bucket (KON-380).
/// <para>
/// The editor's own ceiling used to be here too: a <c>TextBox</c> lays out every line it is given,
/// and <c>helm template prometheus/kube-prometheus-stack --include-crds</c> renders 5.2 MB across
/// 82,000 lines, so the view-model clipped what it handed the page at 512 KB. KON-382 replaced the
/// <c>TextBox</c> with a virtualising editor and the view-model went back to holding one whole
/// bundle; what the editor does with it is covered by <c>ManifestEditorRenderTests</c>.
/// </para>
/// </summary>
public class ApplyManifestEditorTests
{
    private static ApplyManifestViewModel Vm() => new(new FakeClusterEngine(), "kind-test");

    /// <summary>
    /// Fifty resources that could not be previewed are not fifty problems. They get their own chip,
    /// they do not count as failures, and — like the no-ops — a long plan starts with them folded.
    /// </summary>
    [Fact]
    public void Deferred_resources_get_their_own_chip_and_do_not_block_apply()
    {
        var vm = Vm();

        foreach (var i in Enumerable.Range(0, 12))
            vm.Plan.Add(Row(ApplyAction.WouldCreate, $"cm-{i}"));
        foreach (var i in Enumerable.Range(0, 50))
            vm.Plan.Add(Row(ApplyAction.Deferred, $"rule-{i}"));

        vm.HasPlan = true;
        vm.IsPreview = true;

        Assert.Contains(vm.Plan, r => r.Outcome == PlanOutcome.Deferred);
        Assert.DoesNotContain(vm.Plan, r => r.IsFailed);
        Assert.True(vm.CanApply);
    }

    [Fact]
    public void A_deferred_row_explains_itself_rather_than_reading_as_an_error()
    {
        var row = Row(ApplyAction.Deferred, "node-rules", "the CRD for PrometheusRule is installed by this bundle");

        Assert.Equal(PlanOutcome.Deferred, row.Outcome);
        Assert.False(row.IsFailed);
        Assert.False(row.IsChange);
        Assert.Equal("not previewed", row.Tag);
        Assert.Contains("installed by this bundle", row.Subtitle, StringComparison.Ordinal);
    }

    private static ApplyPlanRow Row(ApplyAction action, string name, string? error = null) =>
        new(new ApplyProgress
        {
            Resource = new ResourceRef(new GroupVersionKind("monitoring.coreos.com", "v1", "PrometheusRule"),
                "monitoring", name),
            Action = action,
            Error = error,
        });
}
