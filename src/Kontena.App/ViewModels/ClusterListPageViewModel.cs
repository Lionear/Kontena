using CommunityToolkit.Mvvm.ComponentModel;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.ViewModels;

/// <summary>
/// The following half of a cluster list page, without its row type. Anything that cares whether a
/// page is keeping up with the cluster — a check across every page, a shared header — should not
/// have to know what the page lists to ask.
/// </summary>
public interface IClusterListPage : IDisposable
{
    /// <inheritdoc cref="ClusterListPageViewModel{TRow}.IsLive"/>
    bool IsLive { get; }

    /// <inheritdoc cref="ClusterListPageViewModel{TRow}.LiveNotice"/>
    string? LiveNotice { get; }

    /// <inheritdoc cref="ClusterListPageViewModel{TRow}.WatchedKind"/>
    GroupVersionKind? WatchedKind { get; }

    /// <inheritdoc cref="ClusterListPageViewModel{TRow}.Changed"/>
    Action? Changed { get; set; }

    /// <inheritdoc cref="ClusterListPageViewModel{TRow}.StartWatching"/>
    void StartWatching();
}

/// <summary>
/// A cluster list page that keeps itself up to date (KON-250).
/// <para>
/// <c>IClusterEngine.WatchAsync</c> and <c>ClusterCapabilities.Watch</c> existed from the day the OAL
/// landed and nothing called either — the same shape as <c>Capabilities.Crds</c> before KON-75, and
/// the same rule applies: a flag that steers nothing is not a feature. Every list in the app was
/// list-plus-Refresh, which never lies but is always old, and it is old exactly while a rollout is
/// happening — which is when you are looking at it.
/// </para>
/// <para>
/// A watch event triggers a reload through the page's own typed lister rather than being applied to
/// the rows directly. The stream carries a manifest, not the model the grid is built from, so
/// translating events into rows would mean a second mapping alongside the adapter's — two ways to
/// build one row, disagreeing the first time either changes. The reload is cheap and the reconcile
/// is what keeps it from showing.
/// </para>
/// </summary>
public abstract partial class ClusterListPageViewModel<TRow> : ListPageViewModel<TRow>, IClusterListPage
{
    private readonly IClusterEngine _cluster;
    private readonly GroupVersionKind? _kind;
    private readonly string? _namespace;
    private readonly string? _unwatchable;
    private CancellationTokenSource? _watch;

    /// <summary>
    /// How long to wait after an event before reloading.
    /// <para>
    /// A rollout produces a burst — one event per pod, several per pod — and reloading on each would
    /// be a dozen round trips to draw the same list. Long enough to collapse a burst, short enough
    /// that it still reads as live.
    /// </para>
    /// </summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(400);

    /// <param name="kind">The kind to follow, or null when this page has no single kind to follow.</param>
    /// <param name="unwatchable">Why not, when <paramref name="kind"/> is null.</param>
    protected ClusterListPageViewModel(
        IClusterEngine cluster, GroupVersionKind? kind, string? ns, string? unwatchable = null)
    {
        _cluster = cluster;
        _kind = kind;
        _namespace = ns;
        _unwatchable = unwatchable;
    }

    /// <summary>
    /// The kind this page follows, or null when it has none to follow. Readable because it is a claim
    /// on the adapter: an adapter with no watcher for it hands back an empty stream, and the page
    /// then blames the cluster for closing something nobody opened.
    /// </summary>
    public GroupVersionKind? WatchedKind => _kind;

    /// <summary>Whether this page is currently following the cluster rather than a stale snapshot.</summary>
    [ObservableProperty] private bool _isLive;

    /// <summary>
    /// Told after a watch event has been folded in, for whatever else on screen was reading the same
    /// cluster (KON-339). The sidebar's counts sit beside this list and were not following anything,
    /// so a workload that appeared showed up as a new row next to a badge that still said the old
    /// number — two figures contradicting each other, which is worse than one that is merely old.
    /// <para>
    /// A callback the shell sets, like <c>RequestConfirm</c> elsewhere, rather than the page reaching
    /// for the counts itself: a list page knows what it lists and nothing about what else is drawn.
    /// </para>
    /// </summary>
    public Action? Changed { get; set; }

    /// <summary>
    /// Why it is not live, when it is not. Never null-and-silent: a list that has quietly stopped
    /// moving is indistinguishable from a cluster where nothing is happening, and those two want
    /// opposite reactions.
    /// </summary>
    [ObservableProperty] private string? _liveNotice;

    /// <summary>Start following the cluster. Safe to call more than once.</summary>
    public void StartWatching()
    {
        if (_watch is not null)
            return;

        if (_kind is null)
        {
            LiveNotice = _unwatchable;
            return;
        }

        if (!_cluster.Capabilities.Watch)
        {
            LiveNotice = "This cluster does not support watching, so this list updates when you refresh it.";
            return;
        }

        _watch = new CancellationTokenSource();
        IsLive = true;
        LiveNotice = null;
        _ = FollowAsync(_watch.Token);
    }

    private async Task FollowAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var _ in _cluster.WatchAsync(_kind!.Value, _namespace, ct))
            {
                // Collapse the burst: keep draining until the stream goes quiet for Settle, then
                // reload once. Task.Delay is the wait; the enumerator resumes on the UI thread.
                await Task.Delay(Settle, ct);
                await LoadAsync();
                Changed?.Invoke();
            }

            // The stream ended without being cancelled. An apiserver closes a watch on its own
            // schedule, and a list that silently stops moving is the failure this notice exists for.
            if (!ct.IsCancellationRequested)
                Stopped("The cluster closed the update stream. Refresh to see the current state.");
        }
        catch (OperationCanceledException)
        {
            // Navigating away. Not a failure and not worth a notice.
        }
        catch (Exception failure)
        {
            Stopped($"Live updates stopped — {failure.Message}");
        }
    }

    private void Stopped(string reason)
    {
        IsLive = false;
        LiveNotice = reason;
    }

    /// <summary>
    /// Stop following. Cluster pages are rebuilt on every visit, so a watch that outlived its page
    /// would be a stream nobody reads holding a connection open for the life of the app.
    /// </summary>
    public void Dispose()
    {
        _watch?.Cancel();
        _watch?.Dispose();
        _watch = null;
        IsLive = false;
        GC.SuppressFinalize(this);
    }
}
