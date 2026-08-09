using Kontena.App.ViewModels;
using Kontena.Core.Models;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// Usage graphs on the pod detail (KON-345): the placement setting decides which of the three hosts
/// draws them, and the range selector only offers what the live buffer can actually answer.
/// </summary>
public sealed class PodUsageGraphTests
{
    private static Pod SamplePod() => new()
    {
        Name = "payments-api-7d4f9-x2k1",
        Namespace = "payments",
        Phase = PodPhase.Running,
        Containers = [new ContainerStatus { Name = "api", Image = "payments-api:1.0", Ready = true }],
    };

    private static TerminalFont Font() => new("JetBrains Mono", 13, true);

    private static ClusterPodDetailViewModel Detail(UsageGraphPlacement placement, int minutes = 15) =>
        new(new FakeClusterEngine(), SamplePod(), Font(),
            usageGraphs: new UsageGraphOptions(placement, minutes));

    [Theory]
    [InlineData(UsageGraphPlacement.MetricsTab, true, false, false)]
    [InlineData(UsageGraphPlacement.Sparkline, false, true, false)]
    [InlineData(UsageGraphPlacement.Overview, false, false, true)]
    public void Exactly_one_placement_hosts_the_charts(
        UsageGraphPlacement placement, bool tab, bool sparkline, bool overview)
    {
        using var detail = Detail(placement);

        Assert.Equal(tab, detail.ShowMetricsTab);
        Assert.Equal(sparkline, detail.ShowSparklines);
        Assert.Equal(overview, detail.ShowInlineCharts);
    }

    [Fact]
    public void A_range_the_live_buffer_cannot_answer_is_offered_but_not_selectable()
    {
        // Shown and disabled rather than hidden: the greyed-out 24h is what explains that history
        // needs a source, and a selector that stops at 15m explains nothing.
        using var detail = Detail(UsageGraphPlacement.MetricsTab);

        var day = Assert.Single(detail.RangeOptions, o => o.Minutes == 1440);
        Assert.False(day.IsAvailable);

        detail.SelectRangeCommand.Execute(1440);
        Assert.Equal(15, detail.RangeMinutes);

        detail.SelectRangeCommand.Execute(5);
        Assert.Equal(5, detail.RangeMinutes);
        Assert.Equal("5m", detail.RangeLabel);
    }

    [Fact]
    public void The_selected_range_is_the_one_marked_in_the_selector()
    {
        using var detail = Detail(UsageGraphPlacement.MetricsTab, minutes: 5);

        var selected = Assert.Single(detail.RangeOptions, o => o.IsSelected);
        Assert.Equal(5, selected.Minutes);
    }

    [Fact]
    public async Task Samples_from_the_stream_reach_both_charts()
    {
        using var detail = Detail(UsageGraphPlacement.MetricsTab);

        Assert.True(detail.UsageIsEmpty);

        for (var i = 0; i < 200 && detail.CpuSamples.Count < 3; i++)
            await Task.Delay(5);

        Assert.Equal(3, detail.CpuSamples.Count);
        Assert.Equal(3, detail.MemSamples.Count);
        Assert.False(detail.UsageIsEmpty);

        // The subtitle is the chart's own summary of what it drew, so it must follow the samples
        // rather than repeat the bare unit it starts as.
        Assert.Contains("peak", detail.CpuSubText, StringComparison.Ordinal);
        Assert.Contains("peak", detail.MemSubText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(UsageGraphPlacement.MetricsTab)]
    [InlineData(UsageGraphPlacement.Sparkline)]
    [InlineData(UsageGraphPlacement.Overview)]
    public void A_cluster_without_a_metrics_source_draws_no_charts_at_all(UsageGraphPlacement placement)
    {
        // Whichever placement is chosen, no source means no charts — not empty frames. Same rule the
        // usage gauges already follow (ClusterCapabilities.Metrics).
        using var detail = new ClusterPodDetailViewModel(
            new FakeClusterEngine(metrics: false), SamplePod(), Font(),
            usageGraphs: new UsageGraphOptions(placement, 15));

        Assert.False(detail.ShowSparklines);
        Assert.False(detail.ShowMetricsTab);
        Assert.False(detail.ShowInlineCharts);
    }
}
