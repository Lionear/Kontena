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

    /// <summary>Evenly spaced points — the spacing is not what these tests are about.</summary>
    private static IReadOnlyList<UsagePoint> Points(params double[] values) =>
        [.. values.Select((v, i) => new UsagePoint(values.Length == 1 ? 1 : (double)i / (values.Length - 1), v))];

    private static void Settle()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static UsageChart Show(
        IReadOnlyList<UsagePoint>? values, bool axes = true,
        UsageChartUnit unit = UsageChartUnit.Millicores)
    {
        var chart = new UsageChart
        {
            Values = values,
            ShowAxes = axes,
            Unit = unit,
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
        var (min, max) = UsageChart.Band(Points(512, 512, 512));

        Assert.True(max > min, $"band collapsed: {min}..{max}");
        Assert.InRange(512, min, max);
    }

    [Fact]
    public void An_all_zero_series_gets_a_band_too()
    {
        var (min, max) = UsageChart.Band(Points(0, 0));

        Assert.True(max > min, $"band collapsed: {min}..{max}");
    }

    [Fact]
    public void The_band_leaves_headroom_on_both_sides_of_the_data()
    {
        var (min, max) = UsageChart.Band(Points(100, 200));

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
                var chart = Show(Points(values));

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
                var chart = Show(Points(0, 1, 2, 3, 4));

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
    public Task A_byte_axis_label_is_never_clipped_by_the_gutter() =>
        Session.Dispatch(
            () =>
            {
                // The gutter used to be a fixed 44px, fitted to "250m". A memory axis reading
                // "659.1 MB" started at a negative x and ClipToBounds ate the leading digit, so the
                // chart showed "59.1 MB" — a plausible number, wrong by a factor of ten.
                var bytes = Show(Points(624_000_000, 640_000_000, 654_600_000), unit: UsageChartUnit.Bytes);
                Assert.True(bytes.LeftmostLabelX() >= 0,
                    $"byte label starts at {bytes.LeftmostLabelX()}, so it is cropped");

                var millicores = Show(Points(105, 178, 231), unit: UsageChartUnit.Millicores);
                Assert.True(millicores.LeftmostLabelX() >= 0,
                    $"millicore label starts at {millicores.LeftmostLabelX()}, so it is cropped");

                // The widths themselves are not asserted — headless measures text with a stub glyph
                // advance, so they are not the app's numbers. The relationship is: "659.1 MB" needs
                // more room than "250m", and the gutter has to follow the label rather than a guess.
                Assert.True(bytes.Gutter() > millicores.Gutter(),
                    $"byte gutter {bytes.Gutter()} did not grow past millicore gutter {millicores.Gutter()}");
            },
            CancellationToken.None);

    // ── Gaps and thresholds ──────────────────────────────────────────────────

    [Fact]
    public void A_gap_in_the_source_breaks_the_line_instead_of_bridging_it()
    {
        // Six points at a steady spacing with the middle two hours missing. Drawn as one figure,
        // that gap becomes a straight line — a claim that the value held steady across exactly the
        // stretch nothing was recorded.
        IReadOnlyList<UsagePoint> values =
        [
            new(0.00, 10), new(0.05, 12), new(0.10, 11),
            new(0.80, 40), new(0.85, 42), new(0.90, 41),
        ];

        Assert.Equal([(0, 2), (3, 5)], UsageChart.Segments(values));
    }

    [Fact]
    public void An_evenly_spaced_series_is_one_unbroken_line()
    {
        Assert.Equal([(0, 4)], UsageChart.Segments(Points(1, 2, 3, 4, 5)));
    }

    [Fact]
    public void A_lone_point_between_two_gaps_still_gets_drawn()
    {
        IReadOnlyList<UsagePoint> values =
        [
            new(0.00, 10), new(0.02, 11),
            new(0.50, 30),
            new(0.98, 20), new(1.00, 21),
        ];

        Assert.Equal([(0, 1), (2, 2), (3, 4)], UsageChart.Segments(values));
    }

    [Fact]
    public void A_nearby_limit_is_pulled_into_the_band()
    {
        // "654 MB" means little; "654 MB against a 700 MB limit" is the whole question.
        var (_, max) = UsageChart.Band(Points(600, 640, 654), threshold: 700);

        Assert.True(max >= 700, $"the limit fell outside the band: {max}");
    }

    [Fact]
    public void A_limit_far_above_the_data_is_left_out_of_the_band()
    {
        // A pod at 5% of its limit would otherwise flatten to a line along the bottom, trading the
        // shape the chart is for against a number the readout already gives.
        var (_, withLimit) = UsageChart.Band(Points(600, 640, 654), threshold: 8000);
        var (_, without) = UsageChart.Band(Points(600, 640, 654));

        Assert.Equal(without, withLimit);
    }

    [Fact]
    public Task A_sparkline_has_no_crosshair() =>
        Session.Dispatch(
            () =>
            {
                // No axes means no gutter and no hover: at 78×20 in the header strip there is nowhere
                // to put a readout, and the numbers are already spelled out beside it.
                var chart = Show(Points(0, 1, 2, 3, 4), axes: false);

                Assert.Equal(-1, chart.HoverIndex);
                Assert.Equal(0, chart.HitTest(0));
            },
            CancellationToken.None);
}
