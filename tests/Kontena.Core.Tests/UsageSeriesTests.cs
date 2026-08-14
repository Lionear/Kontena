using Kontena.Core.Models;

namespace Kontena.Core.Tests;

/// <summary>
/// The in-session sample buffer behind the pod usage charts (KON-345). Worth pinning down because
/// the two rules it enforces are both invisible in the drawing: a repeated scrape must not become a
/// second point, and the buffer must not grow for as long as a pod page stays open.
/// </summary>
public sealed class UsageSeriesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_same_scrape_arriving_twice_is_one_point()
    {
        // The poll runs every 15s; metrics-server rescrapes more slowly, so the identical sample
        // comes back. Charting it again would draw a flat run that never happened.
        var series = new UsageSeries(TimeSpan.FromMinutes(15));

        series.Add(T0, 100);
        series.Add(T0, 100);
        series.Add(T0, 250);

        Assert.Equal(1, series.Count);
        Assert.Equal(100, series.Latest);
    }

    [Fact]
    public void A_sample_older_than_the_last_one_is_ignored()
    {
        var series = new UsageSeries(TimeSpan.FromMinutes(15));

        series.Add(T0, 100);
        series.Add(T0.AddSeconds(-30), 999);

        Assert.Equal(1, series.Count);
        Assert.Equal(100, series.Latest);
    }

    [Fact]
    public void Samples_past_the_retention_window_are_dropped()
    {
        var series = new UsageSeries(TimeSpan.FromMinutes(5));

        for (var i = 0; i < 60; i++)
            series.Add(T0.AddSeconds(i * 15), i);

        // 5 minutes at 15s is 20 intervals; the cutoff is exclusive, so the sample exactly on it stays.
        Assert.Equal(21, series.Count);
        Assert.Equal(T0.AddSeconds(59 * 15) - TimeSpan.FromMinutes(5), series.Oldest);
        Assert.Equal(59, series.Latest);
    }

    [Fact]
    public void The_window_returns_only_what_falls_inside_the_range()
    {
        var series = new UsageSeries(TimeSpan.FromMinutes(15));

        for (var i = 0; i < 40; i++)
            series.Add(T0.AddSeconds(i * 15), i);

        var now = T0.AddSeconds(39 * 15);
        var window = series.Window(TimeSpan.FromMinutes(1), now);

        // One minute back from the last sample: that sample plus the four before it. Timestamps
        // come back with them — the chart places a point by when it was taken, not by its index.
        Assert.Equal([35d, 36d, 37d, 38d, 39d], window.Select(w => w.Value));
        Assert.Equal(T0.AddSeconds(35 * 15), window[0].At);
    }

    [Fact]
    public void An_empty_series_charts_nothing_rather_than_throwing()
    {
        var series = new UsageSeries(TimeSpan.FromMinutes(15));

        Assert.Empty(series.Window(TimeSpan.FromMinutes(5), T0));
        Assert.Null(series.Latest);
        Assert.Null(series.Oldest);
    }

    [Fact]
    public void A_window_entirely_in_the_past_is_empty()
    {
        var series = new UsageSeries(TimeSpan.FromMinutes(15));
        series.Add(T0, 100);

        Assert.Empty(series.Window(TimeSpan.FromMinutes(1), T0.AddMinutes(10)));
    }
}
