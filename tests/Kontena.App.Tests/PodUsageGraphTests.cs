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

    private static ClusterPodDetailViewModel Detail(bool metrics = true, bool history = false) =>
        new(new FakeClusterEngine(metrics: metrics) { HasHistory = history }, SamplePod(), Font());

    private static async Task<ClusterPodDetailViewModel> WithHistoryAsync()
    {
        var detail = Detail(history: true);
        for (var i = 0; i < 200 && !detail.Usage.HasHistory; i++)
            await Task.Delay(5);

        Assert.True(detail.Usage.HasHistory, "the fake history source never reported itself available");
        return detail;
    }

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
    public void A_pod_that_is_not_running_charts_nothing_and_says_so()
    {
        // Asserting the empty state on a Running pod raced its own first sample — the stream starts
        // in the constructor. A pod that never streams is the deterministic way to pin it.
        using var detail = new ClusterPodDetailViewModel(
            new FakeClusterEngine(), SamplePod() with { Phase = PodPhase.Pending }, Font());

        Assert.True(detail.Usage.IsEmpty);
        Assert.Empty(detail.Usage.Charts[0].Samples);
        Assert.Equal("—", detail.Usage.Charts[0].NowText);
    }

    [Fact]
    public void A_pod_opens_on_the_whole_live_buffer()
    {
        using var detail = Detail();

        // The range that was picked. What the axis says about it is
        // The_axis_says_how_far_back_the_chart_reaches_not_the_range_that_was_picked — asserting the
        // label here raced the first sample, which is the very thing that changes it.
        Assert.Equal(UsageGraphs.DefaultRangeMinutes, detail.Usage.RangeMinutes);
    }

    [Fact]
    public void A_range_the_live_buffer_cannot_answer_is_offered_but_not_selectable()
    {
        // Shown and disabled rather than hidden: the greyed-out 24h is what explains that history
        // needs a source, and a selector that stops at 15m explains nothing.
        using var detail = Detail();

        var day = Assert.Single(detail.Usage.RangeOptions, o => o.Minutes == 1440);
        Assert.False(day.IsAvailable);

        detail.Usage.SelectRangeCommand.Execute(1440);
        Assert.Equal(15, detail.Usage.RangeMinutes);

        detail.Usage.SelectRangeCommand.Execute(5);
        Assert.Equal(5, detail.Usage.RangeMinutes);
    }

    [Fact]
    public async Task The_axis_says_how_far_back_the_chart_reaches_not_the_range_that_was_picked()
    {
        // A page open for seconds holds seconds of samples. Labelling that axis "15m ago" claimed
        // history that was never sampled — and with the points spread over the full width it looked
        // exactly like a quarter hour of flat data.
        using var detail = Detail();

        for (var i = 0; i < 200 && detail.Usage.Charts[0].Samples.Count < 3; i++)
            await Task.Delay(5);

        // The fake's three samples span 30s, so the axis has to say seconds, not minutes.
        Assert.EndsWith("s", detail.Usage.Charts[0].RangeLabel, StringComparison.Ordinal);
        Assert.NotEqual("15m", detail.Usage.Charts[0].RangeLabel);
    }

    [Fact]
    public void The_selected_range_is_the_one_marked_in_the_selector()
    {
        using var detail = Detail();
        detail.Usage.SelectRangeCommand.Execute(5);

        var selected = Assert.Single(detail.Usage.RangeOptions, o => o.IsSelected);
        Assert.Equal(5, selected.Minutes);
    }

    [Fact]
    public async Task Samples_from_the_stream_reach_both_charts()
    {
        using var detail = Detail();

        for (var i = 0; i < 200 && detail.Usage.Charts[0].Samples.Count < 3; i++)
            await Task.Delay(5);

        Assert.Equal(3, detail.Usage.Charts[0].Samples.Count);
        Assert.Equal(3, detail.Usage.Charts[1].Samples.Count);
        Assert.False(detail.Usage.IsEmpty);

        // The subtitle is the chart's own summary of what it drew, so it must follow the samples
        // rather than repeat the bare unit it starts as.
        Assert.Contains("peak", detail.Usage.Charts[0].SubText, StringComparison.Ordinal);
        Assert.Contains("peak", detail.Usage.Charts[1].SubText, StringComparison.Ordinal);
    }

    // ── History (Prometheus) ─────────────────────────────────────────────────

    [Fact]
    public async Task A_cluster_with_a_history_source_unlocks_the_long_ranges()
    {
        using var detail = await WithHistoryAsync();

        Assert.All(detail.Usage.RangeOptions, o => Assert.True(o.IsAvailable, $"{o.Label} stayed disabled"));

        detail.Usage.SelectRangeCommand.Execute(1440);
        Assert.Equal(1440, detail.Usage.RangeMinutes);
    }

    [Fact]
    public async Task Picking_a_long_range_draws_from_history_instead_of_the_buffer()
    {
        using var detail = await WithHistoryAsync();

        detail.Usage.SelectRangeCommand.Execute(1440);
        for (var i = 0; i < 200 && detail.Usage.Charts[0].Samples.Count <= 3; i++)
            await Task.Delay(5);

        // The live buffer cannot hold more than a handful of samples this early; a day's worth of
        // points can only have come from the history source.
        Assert.True(detail.Usage.Charts[0].Samples.Count > 3, $"only {detail.Usage.Charts[0].Samples.Count} points — buffer, not history");
        Assert.Equal(detail.Usage.Charts[0].Samples.Count, detail.Usage.Charts[1].Samples.Count);
        Assert.Contains("Prometheus", detail.Usage.SourceText, StringComparison.Ordinal);
        Assert.Empty(detail.Usage.UsageError);
    }

    [Fact]
    public async Task The_short_ranges_come_from_history_too_when_there_is_any()
    {
        // They used to be reserved for the live buffer because a 15s poll is fresher than a scrape.
        // On a page open for half a minute that meant three points drawn across a quarter-hour axis,
        // while the same source could answer the whole quarter hour. Freshness of the latest number
        // is the header strip's job; the chart is for shape.
        using var detail = await WithHistoryAsync();

        for (var i = 0; i < 200 && detail.Usage.Charts[0].Samples.Count <= 3; i++)
            await Task.Delay(5);

        Assert.Equal(15, detail.Usage.RangeMinutes);
        Assert.True(detail.Usage.Charts[0].Samples.Count > 3,
            $"15m drew {detail.Usage.Charts[0].Samples.Count} points — that is the buffer, not history");
        Assert.Contains("Prometheus", detail.Usage.SourceText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_short_range_falls_back_to_the_buffer_when_history_has_nothing()
    {
        // A pod created a minute ago has no stored series yet. Drawing "returned nothing" over a
        // buffer that does have points would hide the only data there is.
        var engine = new FakeClusterEngine { HasHistory = true, HistoryIsEmpty = true };
        using var detail = new ClusterPodDetailViewModel(engine, SamplePod(), Font());

        for (var i = 0; i < 400 && detail.Usage.Charts[0].Samples.Count < 3; i++)
            await Task.Delay(5);

        Assert.Equal(3, detail.Usage.Charts[0].Samples.Count);
        Assert.Empty(detail.Usage.UsageError);
        Assert.DoesNotContain("Prometheus", detail.Usage.SourceText, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_history_source_the_hint_explains_the_disabled_ranges()
    {
        using var detail = Detail();

        Assert.False(detail.Usage.HasHistory);
        Assert.Contains("keeps no history", detail.Usage.RangeHint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Narrowing_the_range_narrows_what_both_the_tab_and_the_sparkline_show()
    {
        // One range for both: the sparkline reads the same CpuSamples the tab does, so a narrowed
        // range cannot leave the two telling different stories about the same pod.
        using var detail = Detail();

        for (var i = 0; i < 200 && detail.Usage.Charts[0].Samples.Count < 3; i++)
            await Task.Delay(5);

        // The fake's three samples are 15s apart, so a 5-minute window still holds all of them —
        // what matters is that the window is recomputed rather than left at the old range's result.
        var before = detail.Usage.Charts[0].Samples;
        detail.Usage.SelectRangeCommand.Execute(5);

        Assert.NotSame(before, detail.Usage.Charts[0].Samples);
        Assert.Equal(before.Count, detail.Usage.Charts[0].Samples.Count);
    }
}
