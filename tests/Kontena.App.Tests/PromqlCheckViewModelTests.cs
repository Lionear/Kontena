using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration;
using Xunit;

namespace Kontena.App.Tests;

/// <summary>
/// The PromQL check-and-preview block (KON-209), against <see cref="FakeAlertSource"/> — the same
/// three states <c>FakeAlertSourceTests</c> covers on the source itself, read back through the
/// view-model's chip/summary/sample surface.
/// </summary>
public sealed class PromqlCheckViewModelTests
{
    private static PromqlCheckViewModel Vm() =>
        new(new FakeAlertSource()) { Debounce = TimeSpan.FromMilliseconds(10) };

    [Fact]
    public async Task A_broken_expression_shows_Prometheus_own_error_in_the_error_chip()
    {
        var vm = Vm();
        vm.Expression = "sum(rate(foo{job=\"x\"[5m]))";
        await vm.Settled;

        Assert.True(vm.HasError);
        Assert.Equal("error", vm.ChipText);
        Assert.Equal("Danger", vm.ChipBrushKey);
        Assert.False(vm.HasSamples);
        Assert.NotEmpty(vm.Summary);
    }

    [Fact]
    public async Task A_typo_that_parses_and_matches_nothing_is_still_the_parses_chip_but_the_warning_colour()
    {
        var vm = Vm();
        vm.Expression = "up{jobb=\"checkout\"}";
        await vm.Settled;

        Assert.False(vm.HasError);
        Assert.True(vm.MatchesNothing);
        Assert.Equal("parses", vm.ChipText);
        Assert.Equal("Warn", vm.ChipBrushKey);
        Assert.Contains("0 series match", vm.Summary, StringComparison.Ordinal);
        Assert.False(vm.HasSamples);
    }

    [Fact]
    public async Task A_matching_expression_lists_its_series_with_their_values()
    {
        var vm = Vm();
        vm.Expression = "sum(rate(http_requests_total{job=\"checkout\"}[5m]))";
        await vm.Settled;

        Assert.Equal("parses", vm.ChipText);
        Assert.Equal("Success", vm.ChipBrushKey);
        Assert.True(vm.HasSamples);
        Assert.Equal(2, vm.Samples.Count);
        Assert.Contains(vm.Samples, s => s.LabelText.Contains("checkout-6b4-d92wq", StringComparison.Ordinal));
        Assert.Contains("2 series match", vm.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clearing_the_expression_hides_the_block_instead_of_keeping_a_stale_result()
    {
        var vm = Vm();
        vm.Expression = "up";
        await vm.Settled;
        Assert.True(vm.HasResult);

        vm.Expression = string.Empty;
        Assert.False(vm.HasResult);
    }

    [Fact]
    public async Task Only_the_last_keystroke_of_a_burst_reaches_Prometheus()
    {
        var vm = Vm();
        vm.Expression = "u";
        vm.Expression = "up";
        vm.Expression = "up{jobb=\"checkout\"}";
        await vm.Settled;

        Assert.True(vm.MatchesNothing);
    }

    [Fact]
    public async Task No_source_reachable_says_so_instead_of_pretending_the_expression_is_fine()
    {
        var vm = new PromqlCheckViewModel(NoAlertSource.Instance) { Debounce = TimeSpan.Zero };
        vm.Expression = "up";
        await vm.Settled;

        Assert.True(vm.HasError);
        Assert.Equal("No Prometheus is reachable from this cluster.", vm.Summary);
    }
}
