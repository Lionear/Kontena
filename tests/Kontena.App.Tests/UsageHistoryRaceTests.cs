using Kontena.App.Controls;
using Kontena.App.ViewModels;
using Kontena.Sdk.Orchestration;

namespace Kontena.App.Tests;

/// <summary>
/// A live sample arriving while a history query is still out must not leave the chart on the buffer
/// (KON-425).
/// <para>
/// <c>LoadHistoryAsync</c> used to plot each chart as its series came back and only mark the picture
/// as history's after the last one. The await in between is a real gap: a sample landing in it saw
/// that history had not claimed the picture, refreshed every chart from the buffer, and then the
/// flag went up anyway — so the page sat on three live points under a chip saying Prometheus, and
/// nothing put it right until the source's refresh interval was up.
/// </para>
/// <para>
/// Driven by a source that only answers when this test says so, rather than by timing: the bug was
/// found as a test that failed about once in a thousand runs, and reproducing it by racing it would
/// have inherited exactly that.
/// </para>
/// </summary>
public sealed class UsageHistoryRaceTests
{
    /// <summary>A history source whose queries finish when the test releases them.</summary>
    private sealed class HeldHistory : IMetricsHistory
    {
        private readonly List<TaskCompletionSource<IReadOnlyList<UsageSample>>> _pending = [];

        public string Name => "Prometheus";
        public bool IsAvailable => true;
        public bool Supports(UsageScope scope) => true;
        public ValueTask<bool> ProbeAsync(CancellationToken ct = default) => ValueTask.FromResult(true);
        public TimeSpan RefreshInterval(TimeSpan range) => TimeSpan.FromSeconds(30);

        /// <summary>How many queries are waiting to be answered.</summary>
        public int Outstanding => _pending.Count;

        public ValueTask<IReadOnlyList<UsageSample>> GetHistoryAsync(
            UsageTarget target, UsageMetric metric, TimeSpan range, CancellationToken ct = default)
        {
            var pending = new TaskCompletionSource<IReadOnlyList<UsageSample>>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            _pending.Add(pending);
            return new ValueTask<IReadOnlyList<UsageSample>>(pending.Task);
        }

        /// <summary>Answer the oldest outstanding query with a series of <paramref name="points"/>.</summary>
        public void Answer(int points)
        {
            var pending = _pending[0];
            _pending.RemoveAt(0);

            var now = DateTimeOffset.UtcNow;
            pending.SetResult(
                [.. Enumerable.Range(0, points).Select(i => new UsageSample(now.AddSeconds(-30 * (points - i)), 100 + i))]);
        }
    }

    private static UsageTrackViewModel Track(HeldHistory history) => new(
        [
            new UsageChartSpec("CPU", UsageChartUnit.Millicores, "Accent", UsageMetric.Cpu, "millicores"),
            new UsageChartSpec("Memory", UsageChartUnit.Bytes, "Accent2", UsageMetric.Memory, "bytes"),
        ],
        UsageTarget.Pod("payments", "payments-api-7d4f9-x2k1"),
        history,
        "metrics-server");

    [Fact]
    public async Task A_sample_landing_between_two_history_queries_does_not_redraw_from_the_buffer()
    {
        var history = new HeldHistory();
        var track = Track(history);

        // Three live readings, as a page open for half a minute would have.
        var start = DateTimeOffset.UtcNow.AddSeconds(-30);
        track.Add(start, 120, 400);
        track.Add(start.AddSeconds(15), 130, 410);
        track.Add(start.AddSeconds(30), 140, 420);

        // The probe comes back: history takes over, and the first chart's query goes out.
        var probe = track.ProbeAsync();
        Assert.True(await Eventually(() => history.Outstanding == 1), "the first query never went out");

        // CPU answers with a proper series; Memory's query is still out. This is the gap.
        history.Answer(points: 60);
        Assert.True(await Eventually(() => history.Outstanding == 1), "the second query never went out");

        // A live tick, exactly in it.
        track.Add(start.AddSeconds(45), 150, 430);

        history.Answer(points: 60);
        await probe;

        // Waited on the chip rather than on the points: the load names its source as the last thing
        // it does whether it drew well or badly, so this is "the load has finished" and not half of
        // the assertion below.
        Assert.True(
            await Eventually(() => track.SourceText.Contains("Prometheus", StringComparison.Ordinal)),
            "the history load never finished");

        // Both charts on the series that was answered, not on the four points the buffer holds.
        Assert.Equal(60, track.Charts[0].Samples.Count);
        Assert.Equal(60, track.Charts[1].Samples.Count);
        Assert.Empty(track.UsageError);
    }

    /// <summary>
    /// Let the queued continuations run. The view model's history load is fire-and-forget from a
    /// property change, so there is no task here to await for it.
    /// </summary>
    private static async Task<bool> Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (condition())
                return true;

            await Task.Delay(5);
        }

        return false;
    }
}
