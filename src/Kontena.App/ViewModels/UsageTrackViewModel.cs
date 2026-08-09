using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.App.Controls;
using Kontena.Core.Models;
using Kontena.Sdk.Orchestration;

namespace Kontena.App.ViewModels;

/// <summary>
/// One measure on one page — a buffer, the samples currently drawn, and the words above them.
/// </summary>
/// <param name="Title">"CPU", "Memory", "Disk".</param>
/// <param name="Unit">How the axis and the readout are formatted.</param>
/// <param name="BrushKey">Theme resource key for the line; one hue per measure, fixed.</param>
/// <param name="Metric">
/// Which history series answers for this measure, or null where none does — a node's disk has a
/// live figure and no stored one, and the chart says so by staying on the buffer.
/// </param>
/// <param name="Unavailable">What the subtitle reads before any sample has arrived.</param>
/// <param name="Threshold">
/// The ceiling this measure is read against — a memory limit, a node's capacity — or null. Drawn as
/// a dashed rule so "654 MB" becomes "654 MB of 1 GB".
/// </param>
/// <param name="ThresholdLabel">What the rule is called, e.g. "limit 1.0 GB".</param>
public sealed record UsageChartSpec(
    string Title, UsageChartUnit Unit, string BrushKey, UsageMetric? Metric, string Unavailable,
    double? Threshold = null, string? ThresholdLabel = null);

/// <summary>One chart in a <see cref="UsageTrackViewModel"/>.</summary>
public sealed partial class UsageChartViewModel(UsageChartSpec spec) : ViewModelBase
{
    internal UsageSeries Buffer { get; } = new(UsageGraphs.LiveBuffer);

    public string Title => spec.Title;
    public UsageChartUnit Unit => spec.Unit;
    public string BrushKey => spec.BrushKey;
    internal UsageMetric? Metric => spec.Metric;

    /// <summary>
    /// Settable, not fixed at construction: a container engine only reports the memory limit on the
    /// stats sample, which arrives after the page is built.
    /// </summary>
    [ObservableProperty] private double? _threshold = spec.Threshold;

    [ObservableProperty] private string? _thresholdLabel = spec.ThresholdLabel;

    [ObservableProperty] private IReadOnlyList<UsagePoint> _samples = [];
    [ObservableProperty] private string _subText = spec.Unavailable;
    [ObservableProperty] private string _nowText = "—";

    /// <summary>
    /// Per chart, not per page: on a node the disk stays on the live buffer while CPU and memory
    /// come from history, so one shared label would put "24h" under fifteen minutes of disk.
    /// </summary>
    [ObservableProperty] private string _rangeLabel = Format.Duration(
        TimeSpan.FromMinutes(UsageGraphs.DefaultRangeMinutes));

    /// <summary>Place samples across the plot by when they were taken (KON-347).</summary>
    internal void Plot(IReadOnlyList<(DateTimeOffset At, double Value)> samples, TimeSpan range, DateTimeOffset now)
    {
        if (samples.Count == 0)
        {
            Samples = [];
            Describe();
            return;
        }

        // The span actually drawn, not the one asked for — an axis reading "15m" over thirty
        // seconds of samples claims history that was never taken.
        var first = samples[0].At;
        var span = now - first;
        if (span > range)
            span = range;

        var seconds = span.TotalSeconds;
        Samples = seconds <= 0
            ? [.. samples.Select((x, i) => new UsagePoint(samples.Count == 1 ? 1 : (double)i / (samples.Count - 1), x.Value))]
            : [.. samples.Select(x => new UsagePoint(Math.Clamp(1 - (now - x.At).TotalSeconds / seconds, 0, 1), x.Value))];

        RangeLabel = Format.Duration(span);
        Describe();
    }

    internal void Describe()
    {
        NowText = Samples.Count == 0 ? "—" : Show(Samples[^1].Value);
        SubText = Samples.Count == 0
            ? spec.Unavailable
            : $"{spec.Unavailable} · peak {Show(Samples.Max(p => p.Value))} · avg {Show(Samples.Average(p => p.Value))}";
    }

    private string Show(double value) => Unit switch
    {
        UsageChartUnit.Bytes => ByteSize.Format((long)Math.Round(value)),
        UsageChartUnit.Percent => value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%",
        _ => Math.Round(value).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "m",
    };
}

/// <summary>
/// The usage charts on a detail page (KON-345, KON-347): the buffer of what Kontena sampled, the
/// range selector, and the history source where the cluster has one.
/// <para>
/// Shared by the pod, container, node, workload and namespace pages. Extracted rather than copied:
/// the first version of this lived inside the pod page, and the y-axis label bug found on it would
/// have been reproduced five times over by the time anyone noticed.
/// </para>
/// </summary>
public sealed partial class UsageTrackViewModel : ViewModelBase
{
    private readonly IMetricsHistory _history;
    private readonly UsageTarget _target;
    private readonly string _liveSourceName;
    private readonly string? _historyCaveat;
    private DateTimeOffset _historyFetched = DateTimeOffset.MinValue;

    /// <param name="charts">One per measure, in the order they are drawn.</param>
    /// <param name="target">What the history source should be asked about.</param>
    /// <param name="history">Null where the backend has none — a container engine, say.</param>
    /// <param name="liveSourceName">What answers for "now", named for the UI.</param>
    /// <param name="historyCaveat">
    /// Said in the panel whenever history is drawing, for the cases where the two sources measure
    /// the same thing differently. A node is one: node-exporter counts memory as total minus
    /// available while the kubelet reports a working set, and the two sit about a tenth apart. A
    /// difference the user can see has to be a difference the page explains.
    /// </param>
    public UsageTrackViewModel(
        IEnumerable<UsageChartSpec> charts, UsageTarget target, IMetricsHistory? history,
        string liveSourceName, string? historyCaveat = null)
    {
        _historyCaveat = historyCaveat;
        Charts = [.. charts.Select(c => new UsageChartViewModel(c))];
        _target = target;
        _history = history ?? NoMetricsHistory.Instance;
        _liveSourceName = liveSourceName;
        UpdateSourceText();
    }

    public IReadOnlyList<UsageChartViewModel> Charts { get; }

    /// <summary>Nothing has been sampled yet — an empty frame with no explanation reads as broken.</summary>
    public bool IsEmpty => Charts.Count == 0 || Charts[0].Samples.Count == 0;

    // ── Range ────────────────────────────────────────────────────────────────

    [ObservableProperty] private int _rangeMinutes = UsageGraphs.DefaultRangeMinutes;

    partial void OnRangeMinutesChanged(int value)
    {
        OnPropertyChanged(nameof(RangeOptions));

        // The chip is not updated here: it names what drew the picture, and at this moment nothing
        // has been drawn for the new range yet.
        if (UsesHistory)
            _ = LoadHistoryAsync(force: true);
        else
            Refresh();
    }

    /// <summary>
    /// Every range the selector offers. The long ones are shown and disabled where nothing can
    /// answer them, rather than hidden: "why can I not see yesterday" is the question the greyed-out
    /// buttons and their hint answer, and a selector that stops at 15m does not raise it.
    /// </summary>
    public IReadOnlyList<UsageRangeOption> RangeOptions =>
        [.. UsageGraphs.Ranges.Select(m => new UsageRangeOption(
            m, Format.Duration(TimeSpan.FromMinutes(m)),
            UsageGraphs.IsLive(m) || HasHistory, m == RangeMinutes))];

    [RelayCommand]
    private void SelectRange(int minutes)
    {
        if (UsageGraphs.IsLive(minutes) || HasHistory)
            RangeMinutes = minutes;
    }

    // ── Live samples ─────────────────────────────────────────────────────────

    /// <summary>
    /// Record one reading — one value per chart, in the order they were declared. The buffer keeps
    /// filling even while history owns the picture, so switching back to a short range does not
    /// show a series that starts at the moment of switching.
    /// </summary>
    public void Add(DateTimeOffset at, params double[] values)
    {
        for (var i = 0; i < Charts.Count && i < values.Length; i++)
            Charts[i].Buffer.Add(at, values[i]);

        if (!UsesHistory)
        {
            Refresh();
            return;
        }

        // History is asked again on its own interval, so most ticks find it throttled and do
        // nothing. That is right while history owns the picture — and wrong while the buffer does,
        // which is what left a fallback chart frozen at whatever it held when it fell back.
        if (!_drewFromHistory)
            Refresh();

        _ = LoadHistoryAsync(force: false);
    }

    public void Clear()
    {
        foreach (var chart in Charts)
        {
            chart.Buffer.Clear();
            chart.Samples = [];
            chart.Describe();
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    private void Refresh()
    {
        var range = UsageGraphs.Range(RangeMinutes);
        var now = DateTimeOffset.UtcNow;

        foreach (var chart in Charts)
            chart.Plot(chart.Buffer.Window(range, now), range, now);

        UsageError = string.Empty;
        _drewFromHistory = false;
        UpdateSourceText();
        OnPropertyChanged(nameof(IsEmpty));
    }

    // ── History ──────────────────────────────────────────────────────────────

    /// <summary>Whether a source that remembers answered for this page's scope.</summary>
    [ObservableProperty] private bool _hasHistory;

    partial void OnHasHistoryChanged(bool value)
    {
        OnPropertyChanged(nameof(RangeOptions));
        OnPropertyChanged(nameof(RangeHint));
        UpdateSourceText();

        // Draw from it straight away rather than waiting for the next live tick — the probe coming
        // back is exactly the moment the page can stop showing its two-point buffer.
        if (value)
            _ = LoadHistoryAsync(force: true);
    }

    /// <summary>
    /// Whether the picture comes from history rather than from the live buffer.
    /// <para>
    /// Every range, not only the long ones. The short ranges used to be reserved for the buffer on
    /// the grounds that a 15s poll is fresher than a 30s scrape — true, and the wrong trade: a page
    /// open for half a minute holds three points, so "fresher" meant a triangle where Prometheus
    /// could have drawn sixty points of the same quarter hour. Freshness of the latest reading is
    /// what the header strip is for; the chart is for shape.
    /// </para>
    /// <para>
    /// Never a blend of the two: stitching a 15s tail onto a series answered at a coarser step
    /// draws a spike that is an artefact of two resolutions meeting, not of anything that happened.
    /// </para>
    /// </summary>
    private bool UsesHistory => HasHistory;

    /// <summary>Look for a history source. Safe to call on a page that will never have one.</summary>
    public async Task ProbeAsync(CancellationToken ct = default)
    {
        if (!_history.Supports(_target.Scope) || !Charts.Any(c => c.Metric is not null))
            return;

        try
        {
            var available = await _history.ProbeAsync(ct).ConfigureAwait(true);
            if (!ct.IsCancellationRequested)
                HasHistory = available;
        }
        catch (Exception)
        {
            HasHistory = false;
        }
    }

    /// <param name="force">
    /// True when the user just changed the range and is waiting to see it; false for the periodic
    /// refresh, which honours the source's own interval rather than re-asking on every tick.
    /// </param>
    private async Task LoadHistoryAsync(bool force)
    {
        var range = TimeSpan.FromMinutes(RangeMinutes);
        var now = DateTimeOffset.UtcNow;

        if (!force && now - _historyFetched < _history.RefreshInterval(range))
            return;

        _historyFetched = now;
        var wanted = RangeMinutes;

        try
        {
            var drawn = TimeSpan.Zero;
            var any = false;

            foreach (var chart in Charts)
            {
                if (chart.Metric is not { } metric)
                {
                    // No stored series for this measure; leave it on what the buffer holds. Its own
                    // axis label then says fifteen minutes while its neighbours say a day, which is
                    // the truth rather than an inconsistency.
                    var live = UsageGraphs.Range(RangeMinutes);
                    chart.Plot(chart.Buffer.Window(live, now), live, now);
                    continue;
                }

                var samples = await _history.GetHistoryAsync(_target, metric, range).ConfigureAwait(true);

                // The range can change again while a query is out; the slower answer must not
                // overwrite the chart the user is now looking at.
                if (wanted != RangeMinutes)
                    return;

                chart.Plot([.. samples.Select(s => (s.At, s.Value))], range, now);

                if (samples.Count > 1)
                {
                    any = true;
                    drawn = samples[^1].At - samples[0].At;
                }
            }

            if (!any)
            {
                // Nothing stored yet — a pod created a minute ago, or a scrape that has not seen it.
                // Where the buffer can cover the range itself, draw that instead of an empty frame
                // with an explanation; only say so when neither source has anything.
                // Any sample at all, not two: a probe that comes back after the first readings
                // would otherwise stamp "returned nothing" over a chart that was already drawing.
                if (UsageGraphs.IsLive(RangeMinutes) && Charts.Any(c => c.Buffer.Count > 0))
                {
                    Refresh();
                    return;
                }

                UsageError = $"{_history.Name} returned nothing for this over the last {Format.Duration(range)}.";
                OnPropertyChanged(nameof(IsEmpty));
                return;
            }

            UsageError = string.Empty;
            _drewFromHistory = true;

            UpdateSourceText();
            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (Exception)
        {
            UsageError = $"Could not read history from {_history.Name}.";
        }
    }

    // ── Words ────────────────────────────────────────────────────────────────

    /// <summary>Set when a history query came back empty or failed — an unexplained blank chart is
    /// indistinguishable from something that used nothing.</summary>
    [ObservableProperty] private string _usageError = string.Empty;

    public bool HasError => UsageError.Length > 0;

    partial void OnUsageErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    [ObservableProperty] private string _sourceText = string.Empty;

    /// <summary>
    /// What actually drew the picture on screen, not what was going to. A fallback to the buffer
    /// left the chip naming Prometheus over a chart Prometheus had not supplied.
    /// </summary>
    private bool _drewFromHistory;

    private void UpdateSourceText()
    {
        OnPropertyChanged(nameof(HistoryCaveat));
        OnPropertyChanged(nameof(HasCaveat));

        SourceText = _drewFromHistory
            ? $"{_history.Name} · {Format.Duration(TimeSpan.FromMinutes(RangeMinutes))} at "
              + $"{Format.Duration(_history.RefreshInterval(TimeSpan.FromMinutes(RangeMinutes)))} resolution"
            : $"{_liveSourceName} · sampled live";
    }

    /// <summary>Shown while history is drawing and the two sources are known to differ.</summary>
    public string? HistoryCaveat => _drewFromHistory ? _historyCaveat : null;

    public bool HasCaveat => HistoryCaveat is { Length: > 0 };

    public string RangeHint => HasHistory
        ? $"Every range is read from {_history.Name}. The live readout above still comes from "
          + $"{_liveSourceName}, which is a scrape ahead of the chart."
        : $"Charted from what Kontena sampled since this page was opened — {_liveSourceName} keeps no "
          + "history. Longer ranges need a history source such as Prometheus.";
}
