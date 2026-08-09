using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Kontena.App.Controls;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// The usage chart draws itself (KON-345). Nothing here asserts pixels — headless measures text with
/// a stub advance, so a chart's geometry is not a stable number. What is checked is the arithmetic
/// that decides the shape, and that the awkward inputs reach a real layout without throwing.
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class UsageChartTests(HeadlessSessionFixture headless)
{
    private HeadlessUnitTestSession Session => headless.Session;

    private static void Settle()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static UsageChart Show(IReadOnlyList<double>? values, bool axes = true)
    {
        var chart = new UsageChart
        {
            Values = values,
            ShowAxes = axes,
            Stroke = Brushes.Teal,
            AxisBrush = Brushes.Gray,
            LabelBrush = Brushes.DimGray,
            ReadoutBackground = Brushes.Black,
        };

        var window = new Window { Width = 400, Height = 200, Content = chart };
        window.Show();
        Settle();
        return chart;
    }

    [Fact]
    public void A_flat_series_still_gets_a_band_with_height()
    {
        // Every sample equal is the ordinary case for an idle pod. Without the guard the band is
        // zero high and every point divides by it.
        var (min, max) = UsageChart.Band([512, 512, 512]);

        Assert.True(max > min, $"band collapsed: {min}..{max}");
        Assert.InRange(512, min, max);
    }

    [Fact]
    public void An_all_zero_series_gets_a_band_too()
    {
        var (min, max) = UsageChart.Band([0, 0]);

        Assert.True(max > min, $"band collapsed: {min}..{max}");
    }

    [Fact]
    public void The_band_leaves_headroom_on_both_sides_of_the_data()
    {
        var (min, max) = UsageChart.Band([100, 200]);

        Assert.True(min < 100, $"no room under the low point: {min}");
        Assert.True(max > 200, $"no room over the high point: {max}");
    }

    [Theory]
    [InlineData(new double[0])]
    [InlineData(new[] { 42d })]
    [InlineData(new[] { 0d, 0d, 0d })]
    public Task Awkward_series_lay_out_and_render_without_throwing(double[] values) =>
        Session.Dispatch(
            () =>
            {
                // Empty is what every chart shows for the first 15 seconds, and a single sample is
                // what it shows for the next 15. Both have to survive a real render pass.
                var chart = Show(values);

                Assert.True(chart.Bounds.Width > 0);
                Assert.True(chart.Bounds.Height > 0);
            },
            CancellationToken.None);

    [Fact]
    public Task A_null_series_renders_as_an_empty_frame() =>
        Session.Dispatch(
            () =>
            {
                var chart = Show(null);
                Assert.True(chart.Bounds.Height > 0);
            },
            CancellationToken.None);

    [Fact]
    public Task The_crosshair_picks_the_sample_nearest_the_pointer() =>
        Session.Dispatch(
            () =>
            {
                var chart = Show([0, 1, 2, 3, 4]);

                // Left gutter is 44px and the plot runs to width-6, so five points sit on a 350px
                // span: the first at the gutter, the last at the right edge.
                Assert.Equal(0, chart.HitTest(44));
                Assert.Equal(4, chart.HitTest(chart.Bounds.Width - 6));
                Assert.Equal(2, chart.HitTest(44 + (chart.Bounds.Width - 50) / 2));

                // Outside the plot is no sample rather than the nearest one — a readout pinned to
                // the last point while the pointer is off in the gutter is a lie about where it is.
                Assert.Equal(-1, chart.HitTest(0));
                Assert.Equal(-1, chart.HitTest(chart.Bounds.Width + 40));
            },
            CancellationToken.None);

    [Fact]
    public Task A_sparkline_has_no_crosshair() =>
        Session.Dispatch(
            () =>
            {
                // No axes means no gutter and no hover: at 78×20 in the header strip there is nowhere
                // to put a readout, and the numbers are already spelled out beside it.
                var chart = Show([0, 1, 2, 3, 4], axes: false);

                Assert.Equal(-1, chart.HoverIndex);
                Assert.Equal(0, chart.HitTest(0));
            },
            CancellationToken.None);
}
