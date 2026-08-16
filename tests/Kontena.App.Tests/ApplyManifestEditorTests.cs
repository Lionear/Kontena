using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// The editor's ceiling, and the plan's new quiet bucket (KON-380).
/// <para>
/// <c>helm template prometheus/kube-prometheus-stack --include-crds</c> renders 5.2 MB across 82,000
/// lines — four fifths of it CRD schema. An Avalonia <c>TextBox</c> lays out every line it is given,
/// which measured at over six seconds of frozen window before the cluster had been asked anything.
/// The bundle still applies whole; only what the editor shows is capped.
/// </para>
/// </summary>
public class ApplyManifestEditorTests
{
    private static ApplyManifestViewModel Vm() => new(new FakeClusterEngine(), "kind-test");

    private static string Yaml(int chars) => new('y', chars);

    [Fact]
    public void A_bundle_that_fits_reaches_the_editor_untouched()
    {
        var vm = Vm();
        vm.YamlText = Yaml(1000);

        Assert.False(vm.IsYamlTruncated);
        Assert.Equal(vm.YamlText, vm.EditorText);
    }

    [Fact]
    public void A_bundle_too_big_to_lay_out_is_shown_clipped_but_applied_whole()
    {
        var vm = Vm();
        vm.YamlText = Yaml(3 * 1024 * 1024);

        Assert.True(vm.IsYamlTruncated);
        Assert.Equal(512 * 1024, vm.EditorText.Length);

        // The truth the Apply button uses is the whole thing, not what fits on screen.
        Assert.Equal(3 * 1024 * 1024, vm.YamlText.Length);
    }

    [Fact]
    public void The_note_says_what_is_hidden_so_a_clipped_page_never_reads_as_the_whole_bundle()
    {
        var vm = Vm();
        vm.YamlText = Yaml(3 * 1024 * 1024);

        Assert.Contains("512 KB", vm.TruncationNote, StringComparison.Ordinal);
        Assert.Contains("3 MB", vm.TruncationNote, StringComparison.Ordinal);
    }

    /// <summary>A clipped view is read-only, so a stray edit cannot replace the bundle with its head.</summary>
    [Fact]
    public void Editing_a_clipped_view_cannot_truncate_the_bundle()
    {
        var vm = Vm();
        vm.YamlText = Yaml(3 * 1024 * 1024);

        vm.EditorText = "oops";

        Assert.Equal(3 * 1024 * 1024, vm.YamlText.Length);
    }

    [Fact]
    public void Editing_a_bundle_that_fits_writes_through()
    {
        var vm = Vm();

        vm.EditorText = "kind: ConfigMap";

        Assert.Equal("kind: ConfigMap", vm.YamlText);
    }

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
