namespace Kontena.Core.Models;

/// <summary>
/// One metric's samples over time, kept only as far back as it is worth charting (KON-345).
/// <para>
/// This exists because <c>metrics.k8s.io</c> has no history: it answers with the latest scrape and
/// nothing else, so the only past Kontena can draw is the one it kept while the page was open. A
/// real history source (Prometheus, KON-84) answers a range query instead and does not need this.
/// </para>
/// </summary>
public sealed class UsageSeries(TimeSpan keep)
{
    private readonly List<(DateTimeOffset At, double Value)> _samples = [];

    /// <summary>How far back samples are retained; older ones are dropped as new ones arrive.</summary>
    public TimeSpan Keep { get; } = keep;

    /// <summary>The most recent sample, or null while nothing has arrived yet.</summary>
    public double? Latest => _samples.Count == 0 ? null : _samples[^1].Value;

    /// <summary>When the oldest retained sample was taken, or null when empty.</summary>
    public DateTimeOffset? Oldest => _samples.Count == 0 ? null : _samples[0].At;

    public int Count => _samples.Count;

    /// <summary>
    /// Record a sample. A timestamp that is not newer than the last one is ignored: the poll runs
    /// every 15s but metrics-server only rescrapes every 15–60s, so the same scrape comes back
    /// several times and would otherwise show up as a run of duplicated points.
    /// </summary>
    public void Add(DateTimeOffset at, double value)
    {
        if (_samples.Count > 0 && at <= _samples[^1].At)
            return;

        _samples.Add((at, value));

        var cutoff = at - Keep;
        var stale = 0;
        while (stale < _samples.Count && _samples[stale].At < cutoff)
            stale++;

        if (stale > 0)
            _samples.RemoveRange(0, stale);
    }

    /// <summary>
    /// The samples from the last <paramref name="range"/>, oldest first, with their timestamps.
    /// <para>
    /// Timestamps and not just values: the chart places a point by when it was taken. Spacing them
    /// evenly was fine for a steady poll and became a lie the moment a source with gaps in it could
    /// answer — a run of missing scrapes then drew as a straight line across the time it was missing.
    /// </para>
    /// </summary>
    public IReadOnlyList<(DateTimeOffset At, double Value)> Window(TimeSpan range, DateTimeOffset now)
    {
        if (_samples.Count == 0)
            return [];

        var cutoff = now - range;
        var from = 0;
        while (from < _samples.Count && _samples[from].At < cutoff)
            from++;

        if (from >= _samples.Count)
            return [];

        return _samples.GetRange(from, _samples.Count - from);
    }

    public void Clear() => _samples.Clear();
}
