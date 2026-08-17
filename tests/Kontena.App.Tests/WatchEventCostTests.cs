using System.Collections.Concurrent;
using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// What one watch event is allowed to cost the window (KON-355).
/// <para>
/// The measured symptom was the open cluster page freezing for 150–220 ms at a time, several times a
/// minute, on a cluster where nothing on screen was changing. It was not the reload: that is one call
/// per settled burst and it has to be on the UI thread, because it rebuilds what the UI thread owns.
/// It was the layer under it — the loop reading the streams kept the caller's context, so every event
/// the adapter decoded was decoded <i>on the dispatcher</i>, and events arrive in whatever the
/// connection buffered, so a run of them went through back to back with no wait in between for the
/// window to draw in.
/// </para>
/// <para>
/// So the rule this file holds is: <b>producing an event must not happen on the caller's thread.</b>
/// Asserted on the thread rather than on a clock, for the reason the round-trip budgets are counted
/// rather than timed — a clock in CI measures the runner's mood, and the fake has no decoding cost to
/// measure in the first place. What a fake can hold still is <i>where</i> the work would have landed.
/// </para>
/// </summary>
public sealed class WatchEventCostTests
{
    /// <summary>
    /// A dispatcher small enough to assert against: one thread and one queue, and it says which
    /// thread that is. It has to own a thread rather than hand work to the pool — a context that
    /// dispatches elsewhere stops being <see cref="SynchronizationContext.Current"/> after the first
    /// hop, and then a continuation chain that never left the UI thread in the app looks in the test
    /// exactly like one that did.
    /// </summary>
    private sealed class TestDispatcher : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Work, object? State)> _queue = [];
        private readonly Thread _thread;

        public TestDispatcher()
        {
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var (work, state) in _queue.GetConsumingEnumerable())
                    work(state);
            })
            { IsBackground = true, Name = "test-dispatcher" };

            _thread.Start();
        }

        public int ThreadId => _thread.ManagedThreadId;

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state)
        {
            using var done = new ManualResetEventSlim();
            Post(_ => { try { d(state); } finally { done.Set(); } }, null);
            done.Wait();
        }

        public override SynchronizationContext CreateCopy() => this;

        public void Dispose() => _queue.CompleteAdding();
    }

    [Fact]
    public async Task A_burst_of_events_does_not_put_a_burst_of_work_on_the_caller_s_thread()
    {
        var cluster = new FakeClusterEngine();
        using var ui = new TestDispatcher();

        var reloads = 0;
        var onDispatcher = 0;
        var produced = 0;
        cluster.OnWatchEvent = () =>
        {
            Interlocked.Increment(ref produced);
            if (Environment.CurrentManagedThreadId == ui.ThreadId)
                Interlocked.Increment(ref onDispatcher);
        };

        CancellationTokenSource? watch = null;
        ui.Send(_ => watch = ClusterWatch.Follow(
            cluster,
            [GroupVersionKind.Pod],
            null,
            reload: () =>
            {
                // The redraw is the part that belongs to the caller, and this asserts it still does:
                // it rebuilds collections the UI thread owns, so moving it off would be the opposite
                // bug from the one this file is about.
                Assert.Equal(ui.ThreadId, Environment.CurrentManagedThreadId);
                Interlocked.Increment(ref reloads);
                return Task.CompletedTask;
            },
            onState: (_, _) => { }), null);

        Assert.NotNull(watch);
        using var _ = watch;

        // Let the initial snapshot drain and its settle fire, so what follows is the steady state
        // rather than the page opening.
        await Task.Delay(1200);
        var reloadsBefore = Volatile.Read(ref reloads);

        for (var i = 0; i < 200; i++)
        {
            cluster.EmitWatchEvent(new ResourceEvent
            {
                Type = WatchEventType.Modified,
                Resource = new ResourceRef(GroupVersionKind.Pod, "app", $"api-{i}"),
            });
        }

        await Task.Delay(1200);

        Assert.Equal(200, Volatile.Read(ref produced));
        Assert.Equal(0, Volatile.Read(ref onDispatcher));
        Assert.True(Volatile.Read(ref reloads) > reloadsBefore, "the burst should still have produced a redraw");
    }

    /// <summary>
    /// The other half of the same bill: what the redraw itself asks for. A cluster does not change its
    /// name, its version or its distribution under a page that is already open — the page's own
    /// support check has said so since KON-371 — and yet the reload asked again every time, which in
    /// the Kubernetes adapter is a <c>/version</c> call and a full node listing, once every one to five
    /// seconds, for a heading that was already on screen.
    /// </summary>
    [Fact]
    public async Task Reloading_the_overview_does_not_ask_the_cluster_who_it_is_again()
    {
        var cluster = new FakeClusterEngine();
        using var page = new ClusterOverviewViewModel(cluster);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (page.NodeCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.NotEqual(string.Empty, page.ClusterName);

        // Three more of exactly what a settled watch event does.
        await page.LoadAsync();
        await page.LoadAsync();
        await page.LoadAsync();

        Assert.Equal(1, cluster.CallsTo(nameof(FakeClusterEngine.GetInfoAsync)));

        // The guard on it: four node listings, so the reload is still genuinely reading the cluster
        // and this is a read that was dropped rather than a page that stopped refreshing.
        Assert.Equal(4, cluster.CallsTo(nameof(FakeClusterEngine.ListNodesAsync)));
        Assert.NotEqual(string.Empty, page.Version);
    }
}
