using CommunityToolkit.Mvvm.ComponentModel;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.ViewModels;

/// <summary>
/// A cluster page that keeps itself up to date, without its rows. Anything that cares whether a page
/// is keeping up with the cluster — a check across every page, a shared header — should not have to
/// know what the page shows to ask.
/// <para>
/// Named for being live rather than for being a list since KON-340: the overview and the Workloads
/// dashboard follow the cluster too, and they have no rows at all.
/// </para>
/// </summary>
public interface IClusterLivePage : IDisposable
{
    /// <summary>Whether this page is currently following the cluster rather than a stale snapshot.</summary>
    bool IsLive { get; }

    /// <summary>
    /// Why it is not live, when it is not. Never null-and-silent: a page that has quietly stopped
    /// moving is indistinguishable from a cluster where nothing is happening, and those two want
    /// opposite reactions.
    /// </summary>
    string? LiveNotice { get; }

    /// <summary>
    /// The kinds this page follows, empty when it has none to follow. Readable because it is a claim
    /// on the adapter: an adapter with no watcher for one of them hands back an empty stream, and the
    /// page then blames the cluster for closing something nobody opened.
    /// </summary>
    IReadOnlyList<GroupVersionKind> WatchedKinds { get; }

    /// <summary>
    /// Told after a watch event has been folded in, for whatever else on screen was reading the same
    /// cluster (KON-339). The sidebar's counts sit beside these pages and were not following
    /// anything, so a workload that appeared showed up next to a badge that still said the old
    /// number — two figures contradicting each other, which is worse than one that is merely old.
    /// <para>
    /// A callback the shell sets, like <c>RequestConfirm</c> elsewhere, rather than the page reaching
    /// for the counts itself: a page knows what it shows and nothing about what else is drawn.
    /// </para>
    /// </summary>
    Action? Changed { get; set; }

    /// <summary>Start following the cluster. Safe to call more than once.</summary>
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
public abstract partial class ClusterListPageViewModel<TRow> : ListPageViewModel<TRow>, IClusterLivePage
{
    private readonly IClusterEngine _cluster;
    private readonly string? _namespace;
    private readonly string? _unwatchable;
    private CancellationTokenSource? _watch;

    // Separate from _watch because a cluster that cannot watch hands back no source to hold, and
    // "already tried" is what makes a second call a no-op rather than a second set of streams.
    private bool _started;

    /// <param name="kind">The kind to follow, or null when this page has no single kind to follow.</param>
    /// <param name="unwatchable">Why not, when <paramref name="kind"/> is null.</param>
    protected ClusterListPageViewModel(
        IClusterEngine cluster, GroupVersionKind? kind, string? ns, string? unwatchable = null)
    {
        _cluster = cluster;
        _namespace = ns;
        _unwatchable = unwatchable;
        WatchedKinds = kind is { } k ? [k] : [];
    }

    /// <inheritdoc/>
    public IReadOnlyList<GroupVersionKind> WatchedKinds { get; }

    /// <inheritdoc/>
    [ObservableProperty] private bool _isLive;

    /// <inheritdoc/>
    public Action? Changed { get; set; }

    /// <inheritdoc/>
    [ObservableProperty] private string? _liveNotice;

    /// <inheritdoc/>
    public void StartWatching()
    {
        if (_started)
            return;

        _started = true;

        if (WatchedKinds.Count == 0)
        {
            LiveNotice = _unwatchable;
            return;
        }

        _watch = ClusterWatch.Follow(
            _cluster, WatchedKinds, _namespace,
            reload: async () =>
            {
                await LoadAsync();
                Changed?.Invoke();
            },
            onState: (live, notice) =>
            {
                IsLive = live;
                LiveNotice = notice;
            });
    }

    /// <summary>
    /// Stop following. Cluster pages are rebuilt on every visit, so a watch that outlived its page
    /// would be a stream nobody reads holding a connection open for the life of the app.
    /// </summary>
    public virtual void Dispose()
    {
        _watch?.Cancel();
        _watch?.Dispose();
        _watch = null;
        IsLive = false;
        GC.SuppressFinalize(this);
    }
}
