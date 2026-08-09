using System.Threading.Channels;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.ViewModels;

/// <summary>
/// Following a cluster and redrawing when it moves — for one kind or for several (KON-340).
/// <para>
/// Every list page follows exactly one kind, and that assumption was baked into the follow loop. The
/// pages that summarise a cluster — the overview, the Workloads dashboard — have no single kind to
/// follow, and so they followed nothing at all: one snapshot taken when you opened them, kept until
/// you navigated away and came back. The landing page of a cluster was the most out-of-date thing in
/// the app.
/// </para>
/// <para>
/// Written once, here, rather than as a second follow loop beside the list pages'. Two
/// implementations of "watch, settle, reload, say when you have stopped" would be two sets of rules
/// about when a page is lying about being live, disagreeing the first time either changed.
/// </para>
/// </summary>
internal static class ClusterWatch
{
    /// <summary>
    /// How long to wait after an event before reloading.
    /// <para>
    /// A rollout produces a burst — one event per pod, several per pod, and across several kinds at
    /// once for a page that summarises them. Long enough to collapse a burst, short enough that it
    /// still reads as live.
    /// </para>
    /// </summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(400);

    private const string StreamClosed =
        "The cluster closed the update stream. Refresh to see the current state.";

    /// <summary>
    /// Start following <paramref name="kinds"/>, calling <paramref name="reload"/> once per settled
    /// burst. Returns the source to cancel when the page goes away, or null when this cluster cannot
    /// watch at all — in which case <paramref name="onState"/> has already been told why.
    /// <para>
    /// Nothing here is <c>ConfigureAwait(false)</c>, and that is deliberate: every continuation lands
    /// back on the thread that called this, which is the UI thread, which is the only thread allowed
    /// to touch the collections <paramref name="reload"/> rebuilds.
    /// </para>
    /// </summary>
    /// <param name="onState">Told (isLive, notice) — a notice of null only alongside true. A page that
    /// has quietly stopped following looks exactly like a cluster where nothing is happening, and
    /// those two want opposite reactions.</param>
    public static CancellationTokenSource? Follow(
        IClusterEngine cluster,
        IReadOnlyList<GroupVersionKind> kinds,
        string? ns,
        Func<Task> reload,
        Action<bool, string?> onState)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(kinds);
        ArgumentNullException.ThrowIfNull(onState);

        if (kinds.Count == 0)
            throw new ArgumentException("Nothing to follow.", nameof(kinds));

        if (!cluster.Capabilities.Watch)
        {
            onState(false, "This cluster does not support watching, so this page updates when you refresh it.");
            return null;
        }

        var stop = new CancellationTokenSource();
        onState(true, null);

        // One slot, and a second event arriving while one is pending is dropped: two kinds moving in
        // the same instant are one instruction to redraw, not two. The redraw reads every kind the
        // page summarises regardless of which one moved, so coalescing across kinds loses nothing.
        var signal = Channel.CreateBounded<byte>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

        foreach (var kind in kinds)
            _ = PumpAsync(cluster, kind, ns, signal.Writer, onState, stop.Token);

        _ = DrainAsync(signal.Reader, reload, onState, stop.Token);

        return stop;
    }

    /// <summary>One kind's stream, reduced to "something moved".</summary>
    private static async Task PumpAsync(
        IClusterEngine cluster, GroupVersionKind kind, string? ns,
        ChannelWriter<byte> signal, Action<bool, string?> onState, CancellationToken ct)
    {
        try
        {
            await foreach (var _ in cluster.WatchAsync(kind, ns, ct))
                signal.TryWrite(0);

            // The stream ended without being cancelled. An apiserver closes a watch on its own
            // schedule, and a page that silently stops moving is the failure the notice exists for.
            // One kind going quiet is enough: the page would then be showing a mix of live and
            // frozen numbers, which is worse than a page that admits it is frozen.
            if (!ct.IsCancellationRequested)
                onState(false, StreamClosed);
        }
        catch (OperationCanceledException)
        {
            // Navigating away. Not a failure and not worth a notice.
        }
        catch (Exception failure)
        {
            onState(false, $"Live updates stopped — {failure.Message}");
        }
    }

    /// <summary>The single redraw loop, however many streams feed it.</summary>
    private static async Task DrainAsync(
        ChannelReader<byte> signal, Func<Task> reload, Action<bool, string?> onState, CancellationToken ct)
    {
        try
        {
            while (await signal.WaitToReadAsync(ct))
            {
                signal.TryRead(out _);
                await Task.Delay(Settle, ct);

                // Whatever arrived during the settle is this same redraw; taking the slot now rather
                // than after the reload means an event that lands *during* the reload still gets one.
                signal.TryRead(out _);

                await reload();
            }
        }
        catch (OperationCanceledException)
        {
            // Navigating away.
        }
        catch (Exception failure)
        {
            onState(false, $"Live updates stopped — {failure.Message}");
        }
    }
}
