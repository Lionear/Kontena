using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Sdk.Orchestration;

/// <summary>Which measure a history query is about.</summary>
public enum UsageMetric
{
    /// <summary>CPU in milli-cores.</summary>
    Cpu = 0,

    /// <summary>Memory working set in bytes.</summary>
    Memory,
}

/// <summary>One point in a usage series.</summary>
/// <param name="At">When it was measured (UTC).</param>
/// <param name="Value">Milli-cores or bytes, per the <see cref="UsageMetric"/> asked for.</param>
public readonly record struct UsageSample(DateTimeOffset At, double Value);

/// <summary>
/// A source that can answer for the <b>past</b>, which <see cref="IMetricsSource"/> deliberately
/// cannot (KON-345).
/// <para>
/// Separate interface rather than more methods on <see cref="IMetricsSource"/>, because the two are
/// different capabilities and clusters routinely have one without the other. A metrics-server
/// answers "now" and keeps nothing; a Prometheus keeps weeks and is not what the live gauges should
/// be polling at 15s. Kontena reads both where both exist.
/// </para>
/// </summary>
public interface IMetricsHistory
{
    /// <summary>Short name for the UI, e.g. "Prometheus".</summary>
    string Name { get; }

    /// <summary>
    /// Whether this source can currently answer. False makes the UI keep to the live buffer and
    /// leave the longer ranges disabled, rather than offering a range that returns nothing.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Look for the source and update <see cref="IsAvailable"/>. Must not throw: an absent or
    /// unreachable Prometheus is a false, not an error.
    /// </summary>
    ValueTask<bool> ProbeAsync(CancellationToken ct = default);

    /// <summary>
    /// One pod's usage over the last <paramref name="range"/>, oldest first. Empty when the source
    /// has nothing for that pod — a pod younger than the range, or a scrape that never saw it.
    /// <para>
    /// The resolution is the source's to choose: a range query is answered at whatever step keeps
    /// the series a sensible size, and the caller draws what it is given.
    /// </para>
    /// </summary>
    ValueTask<IReadOnlyList<UsageSample>> GetPodHistoryAsync(
        ResourceRef pod, UsageMetric metric, TimeSpan range, CancellationToken ct = default);

    /// <summary>
    /// How often re-asking is worth it for this range. A 15-minute chart moves every scrape; a
    /// week-long one does not change meaningfully between coffees.
    /// </summary>
    TimeSpan RefreshInterval(TimeSpan range);
}

/// <summary>
/// Implemented by cluster backends that can answer for the past. Optional, like
/// <see cref="IMetricsAware"/>: a backend without it simply has no history, and the UI keeps to
/// what it sampled itself.
/// </summary>
public interface IMetricsHistoryAware
{
    /// <summary>Never null — an absent source is <see cref="NoMetricsHistory"/>.</summary>
    IMetricsHistory History { get; }
}

/// <summary>The null history: a cluster with nothing that remembers.</summary>
public sealed class NoMetricsHistory : IMetricsHistory
{
    public static readonly NoMetricsHistory Instance = new();

    public string Name => "none";
    public bool IsAvailable => false;

    public ValueTask<bool> ProbeAsync(CancellationToken ct = default) => ValueTask.FromResult(false);

    public ValueTask<IReadOnlyList<UsageSample>> GetPodHistoryAsync(
        ResourceRef pod, UsageMetric metric, TimeSpan range, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<UsageSample>>([]);

    public TimeSpan RefreshInterval(TimeSpan range) => TimeSpan.FromMinutes(1);
}
