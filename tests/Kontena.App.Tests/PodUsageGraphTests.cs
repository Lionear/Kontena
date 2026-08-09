using Kontena.App.ViewModels;
using Kontena.Core.Models;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// Usage graphs on the pod detail (KON-345): the header sparkline and the Metrics tab are both
/// there whenever the cluster has a metrics source, and the range selector only offers what the
/// live buffer can actually answer.
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

    private static ClusterPodDetailViewModel Detail(bool metrics = true) =>
        new(new FakeClusterEngine(metrics: metrics), SamplePod(), Font());

    [Fact]
    public void A_cluster_with_metrics_gets_both_the_sparkline_and_the_tab()
    {
        // Not alternatives, and never were worth a setting: one is the glance, the other is where
        // you dig. Making them exclusive forced a choice between two things you want at once.
        using var detail = Detail();

        Assert.True(detail.ShowUsageGraphs);
    }

    [Fact]
    public void A_cluster_without_a_metrics_source_draws_no_charts_at_all()
    {
        // No source means no charts, not empty frames — the rule the usage gauges already follow.
        using var detail = Detail(metrics: false);

        Assert.False(detail.ShowUsageGraphs);
    }

    [Fact]
    public void A_pod_opens_on_the_whole_live_buffer()
    {
        using var detail = Detail();

        Assert.Equal(UsageGraphs.DefaultRangeMinutes, detail.RangeMinutes);
        Assert.Equal("15m", detail.RangeLabel);
    }

    [Fact]
    public void A_range_the_live_buffer_cannot_answer_is_offered_but_not_selectable()
    {
        // Shown and disabled rather than hidden: the greyed-out 24h is what explains that history
        // needs a source, and a selector that stops at 15m explains nothing.
        using var detail = Detail();

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
        using var detail = Detail();
        detail.SelectRangeCommand.Execute(5);

        var selected = Assert.Single(detail.RangeOptions, o => o.IsSelected);
        Assert.Equal(5, selected.Minutes);
    }

    [Fact]
    public async Task Samples_from_the_stream_reach_both_charts()
    {
        using var detail = Detail();

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

    [Fact]
    public async Task Narrowing_the_range_narrows_what_both_the_tab_and_the_sparkline_show()
    {
        // One range for both: the sparkline reads the same CpuSamples the tab does, so a narrowed
        // range cannot leave the two telling different stories about the same pod.
        using var detail = Detail();

        for (var i = 0; i < 200 && detail.CpuSamples.Count < 3; i++)
            await Task.Delay(5);

        // The fake's three samples are 15s apart, so a 5-minute window still holds all of them —
        // what matters is that the window is recomputed rather than left at the old range's result.
        var before = detail.CpuSamples;
        detail.SelectRangeCommand.Execute(5);

        Assert.NotSame(before, detail.CpuSamples);
        Assert.Equal(before.Count, detail.CpuSamples.Count);
    }
}
