using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Kontena.Core.Models;

namespace Kontena.App.Controls;

/// <summary>What the numbers are, so the chart can label its own axis.</summary>
public enum UsageChartUnit
{
    /// <summary>CPU in milli-cores — "122m".</summary>
    Millicores = 0,

    /// <summary>Memory in bytes — formatted by <see cref="ByteSize"/>.</summary>
    Bytes,

    /// <summary>A percentage of one core, the way a container engine reports CPU — "12.4%".</summary>
    Percent,
}

/// <summary>
/// A single-series time chart: line, area wash, three gridlines, and a crosshair on hover
/// (KON-345). Evenly spaced points, because the samples behind it come from a fixed-interval poll.
/// <para>
/// Drawn here rather than pulled in as a chart library. What the pod detail needs is one line on
/// one axis; the smallest of the packages that could supply it brings a rendering stack, a theming
/// layer and a licence to read, none of which this uses.
/// </para>
/// <para>
/// One series per chart, deliberately: CPU and memory share no unit, and putting them on two y-axes
/// in one plot invites reading a crossing as a relationship between them.
/// </para>
/// </summary>
public sealed class UsageChart : Control
{
    /// <summary>Oldest first. Fewer than two points draws the frame and nothing else.</summary>
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<UsageChart, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<UsageChart, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<IBrush?> AxisBrushProperty =
        AvaloniaProperty.Register<UsageChart, IBrush?>(nameof(AxisBrush));

    public static readonly StyledProperty<IBrush?> LabelBrushProperty =
        AvaloniaProperty.Register<UsageChart, IBrush?>(nameof(LabelBrush));

    /// <summary>Behind the crosshair readout, so the line never shows through the digits.</summary>
    public static readonly StyledProperty<IBrush?> ReadoutBackgroundProperty =
        AvaloniaProperty.Register<UsageChart, IBrush?>(nameof(ReadoutBackground));

    public static readonly StyledProperty<UsageChartUnit> UnitProperty =
        AvaloniaProperty.Register<UsageChart, UsageChartUnit>(nameof(Unit));

    /// <summary>Axes, labels and hover. Off is sparkline mode: a bare line at strip height.</summary>
    public static readonly StyledProperty<bool> ShowAxesProperty =
        AvaloniaProperty.Register<UsageChart, bool>(nameof(ShowAxes), true);

    /// <summary>How far back the leftmost point is, e.g. "15m" — labels the x axis.</summary>
    public static readonly StyledProperty<string?> RangeLabelProperty =
        AvaloniaProperty.Register<UsageChart, string?>(nameof(RangeLabel));

    static UsageChart() =>
        AffectsRender<UsageChart>(
            ValuesProperty, StrokeProperty, AxisBrushProperty, LabelBrushProperty,
            ReadoutBackgroundProperty, UnitProperty, ShowAxesProperty, RangeLabelProperty);

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public IBrush? AxisBrush
    {
        get => GetValue(AxisBrushProperty);
        set => SetValue(AxisBrushProperty, value);
    }

    public IBrush? LabelBrush
    {
        get => GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    public IBrush? ReadoutBackground
    {
        get => GetValue(ReadoutBackgroundProperty);
        set => SetValue(ReadoutBackgroundProperty, value);
    }

    public UsageChartUnit Unit
    {
        get => GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public bool ShowAxes
    {
        get => GetValue(ShowAxesProperty);
        set => SetValue(ShowAxesProperty, value);
    }

    public string? RangeLabel
    {
        get => GetValue(RangeLabelProperty);
        set => SetValue(RangeLabelProperty, value);
    }

    /// <summary>Index under the pointer, or -1.</summary>
    internal int HoverIndex { get; private set; } = -1;

    public UsageChart() => ClipToBounds = true;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!ShowAxes)
            return;

        SetHover(HitTest(e.GetPosition(this).X));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHover(-1);
    }

    private void SetHover(int index)
    {
        if (index == HoverIndex)
            return;

        HoverIndex = index;
        InvalidateVisual();
    }

    internal int HitTest(double x)
    {
        var values = Values;
        if (values is null || values.Count < 2)
            return -1;

        var (left, _, width, _) = PlotRect();
        if (width <= 0)
            return -1;

        var step = width / (values.Count - 1);
        var index = (int)Math.Round((x - left) / step);
        return index < 0 || index >= values.Count ? -1 : index;
    }

    private const double AxisLabelSize = 9.5;
    private const double AxisLabelGap = 8;
    private const double MinGutter = 44;

    /// <summary>
    /// Width reserved for the y-axis labels, measured rather than assumed.
    /// <para>
    /// A fixed 44px fitted "250m" and silently cropped "659.1 MB" down to "59.1 MB" — a number that
    /// looks perfectly reasonable and is wrong by an order of magnitude, which is worse than a
    /// visibly broken label. Quantised to 8px so the plot does not shuffle sideways every time the
    /// band ticks over a digit.
    /// </para>
    /// </summary>
    internal double Gutter()
    {
        if (!ShowAxes)
            return 0;

        var values = Values;
        if (values is not { Count: > 0 })
            return MinGutter;

        var widest = 0d;
        foreach (var (text, _) in AxisLabels(Band(values)))
            widest = Math.Max(widest, LabelWidth(text));

        return Math.Max(MinGutter, Math.Ceiling((widest + AxisLabelGap * 1.5) / 8) * 8);
    }

    /// <summary>The three axis labels, top to bottom, with the fraction of the plot they sit at.</summary>
    private static IEnumerable<(string Text, double Fraction)> AxisLabelsCore(
        (double Min, double Max) band, Func<double, string> label)
    {
        for (var g = 0; g <= 2; g++)
            yield return (label(band.Max - (band.Max - band.Min) * g / 2), g / 2d);
    }

    private IEnumerable<(string Text, double Fraction)> AxisLabels((double Min, double Max) band) =>
        AxisLabelsCore(band, Label);

    private double LabelWidth(string text) => Text(text, Brushes.Transparent, AxisLabelSize).Width;

    /// <summary>
    /// The left edge of the widest axis label as it will be drawn. Negative means it is being
    /// clipped — which is the failure this control had, so the tests assert on it directly.
    /// </summary>
    internal double LeftmostLabelX()
    {
        var values = Values;
        if (!ShowAxes || values is not { Count: > 0 })
            return double.NaN;

        var gutter = Gutter();
        var leftmost = double.MaxValue;
        foreach (var (text, _) in AxisLabels(Band(values)))
            leftmost = Math.Min(leftmost, gutter - AxisLabelGap - LabelWidth(text));

        return leftmost;
    }

    private (double Left, double Top, double Width, double Height) PlotRect()
    {
        var gutter = Gutter();
        var top = ShowAxes ? 8d : 2d;
        var bottom = ShowAxes ? 18d : 2d;
        var right = ShowAxes ? 6d : 1d;

        return (gutter, top,
                Math.Max(0, Bounds.Width - gutter - right),
                Math.Max(0, Bounds.Height - top - bottom));
    }

    /// <summary>
    /// The value band the plot spans. Headroom above and below on purpose: a line drawn hard against
    /// the top edge reads as clipped, and one on the baseline reads as zero when it is not.
    /// </summary>
    internal static (double Min, double Max) Band(IReadOnlyList<double> values)
    {
        double lo = values[0], hi = values[0];
        foreach (var v in values)
        {
            if (v < lo) lo = v;
            if (v > hi) hi = v;
        }

        if (hi - lo < double.Epsilon)
        {
            // A flat series still deserves a readable band rather than a divide-by-zero.
            var pad = Math.Abs(hi) < double.Epsilon ? 1 : Math.Abs(hi) * 0.1;
            return (lo - pad, hi + pad);
        }

        var span = hi - lo;
        return (lo - span * 0.35, hi + span * 0.2);
    }

    private string Label(double value) => Unit switch
    {
        UsageChartUnit.Bytes => ByteSize.Format((long)Math.Round(value)),
        UsageChartUnit.Percent => value.ToString("0.0", CultureInfo.InvariantCulture) + "%",
        _ => Math.Round(value).ToString("0", CultureInfo.InvariantCulture) + "m",
    };

    private FormattedText Text(string text, IBrush brush, double size = 10) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(TextElement.GetFontFamily(this)), size, brush);

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var (left, top, width, height) = PlotRect();
        if (width <= 0 || height <= 0)
            return;

        var values = Values;
        var axis = AxisBrush;
        var labels = LabelBrush ?? axis;

        if (ShowAxes && axis is not null)
            RenderFrame(context, values, axis, labels, left, top, width, height);

        if (values is null || values.Count < 2 || Stroke is not { } stroke)
            return;

        var (min, max) = Band(values);
        double X(int i) => left + width * i / (values.Count - 1);
        double Y(double v) => top + height - (v - min) / (max - min) * height;

        var line = new StreamGeometry();
        using (var pen = line.Open())
        {
            pen.BeginFigure(new Point(X(0), Y(values[0])), isFilled: false);
            for (var i = 1; i < values.Count; i++)
                pen.LineTo(new Point(X(i), Y(values[i])));
            pen.EndFigure(false);
        }

        var area = new StreamGeometry();
        using (var fill = area.Open())
        {
            fill.BeginFigure(new Point(X(0), top + height), isFilled: true);
            for (var i = 0; i < values.Count; i++)
                fill.LineTo(new Point(X(i), Y(values[i])));
            fill.LineTo(new Point(X(values.Count - 1), top + height));
            fill.EndFigure(true);
        }

        // The wash is the stroke's own colour faded out downwards, so a chart never needs a second
        // brush declared beside it that could drift from the line it belongs to.
        if (stroke is ISolidColorBrush solid)
        {
            context.DrawGeometry(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(56, solid.Color.R, solid.Color.G, solid.Color.B), 0),
                    new GradientStop(Color.FromArgb(0, solid.Color.R, solid.Color.G, solid.Color.B), 1),
                },
            }, null, area);
        }

        context.DrawGeometry(null, new Pen(stroke, ShowAxes ? 2 : 1.5)
        {
            LineJoin = PenLineJoin.Round,
            LineCap = PenLineCap.Round,
        }, line);

        if (!ShowAxes)
        {
            // The sparkline's only affordance: which end is now.
            context.DrawEllipse(stroke, null, new Point(X(values.Count - 1), Y(values[^1])), 2, 2);
            return;
        }

        if (HoverIndex >= 0 && HoverIndex < values.Count)
            RenderCrosshair(context, values, X(HoverIndex), Y(values[HoverIndex]), top, height, labels);
    }

    private void RenderFrame(
        DrawingContext context, IReadOnlyList<double>? values, IBrush axis, IBrush? labels,
        double left, double top, double width, double height)
    {
        var grid = new Pen(axis, 1);

        for (var g = 0; g <= 2; g++)
        {
            var y = top + height * g / 2;
            context.DrawLine(grid, new Point(left, y), new Point(left + width, y));
        }

        if (labels is null)
            return;

        if (values is { Count: > 0 })
        {
            foreach (var (label, fraction) in AxisLabels(Band(values)))
            {
                var text = Text(label, labels, AxisLabelSize);
                var y = top + height * fraction;

                // left is Gutter(), which was sized off these very labels — so this x is >= 0.
                context.DrawText(text, new Point(left - AxisLabelGap - text.Width, y - text.Height / 2));
            }
        }

        var bottom = top + height + 4;
        if (RangeLabel is { Length: > 0 } range)
            context.DrawText(Text($"{range} ago", labels, AxisLabelSize), new Point(left, bottom));

        var now = Text("now", labels, AxisLabelSize);
        context.DrawText(now, new Point(left + width - now.Width, bottom));
    }

    private void RenderCrosshair(
        DrawingContext context, IReadOnlyList<double> values, double x, double y,
        double top, double height, IBrush? labels)
    {
        context.DrawLine(
            new Pen(labels ?? Brushes.Gray, 1, new DashStyle([3, 3], 0)),
            new Point(x, top), new Point(x, top + height));

        context.DrawEllipse(Stroke, new Pen(ReadoutBackground ?? Brushes.Black, 2), new Point(x, y), 4, 4);

        var text = Text(Label(values[HoverIndex]), labels ?? Brushes.Gray, 11);
        var box = new Rect(
            Math.Clamp(x - text.Width / 2 - 6, 0, Math.Max(0, Bounds.Width - text.Width - 12)),
            Math.Max(0, y - text.Height - 14),
            text.Width + 12, text.Height + 6);

        context.DrawRectangle(ReadoutBackground, new Pen(AxisBrush, 1), box, 5, 5);
        context.DrawText(text, new Point(box.X + 6, box.Y + 3));
    }
}
