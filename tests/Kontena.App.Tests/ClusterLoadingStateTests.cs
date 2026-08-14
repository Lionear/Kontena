using System.ComponentModel;
using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;

namespace Kontena.App.Tests;

/// <summary>
/// The rule the loading indicators follow (KON-375, and KON-319 before it): the first fetch says so,
/// every later one does not.
/// <para>
/// The second half is the part worth pinning. These pages reload on every settled watch event, which
/// on an active cluster is every few seconds — a spinner on each of those is not "loading", it is the
/// flicker <c>ListSync</c> exists to keep off the rows. What is asserted is therefore the absence of
/// a notification, not the value of the flag: a reload that sets it true and false again inside one
/// synchronous call would leave the flag looking untouched afterwards while blinking on screen.
/// </para>
/// <para>
/// The fake answers without yielding, so the first fetch is already over by the time a test can look
/// at it. That the bar is bound to the flag at all is a UI test (<c>ClusterLoadingIndicatorTests</c>);
/// this is only about when the flag moves.
/// </para>
/// </summary>
public sealed class ClusterLoadingStateTests
{
    /// <summary>How many times <c>IsLoading</c> was announced while <paramref name="reload"/> ran.</summary>
    private static int LoadingSignalsDuring(INotifyPropertyChanged page, Func<Task> reload)
    {
        var signals = 0;
        page.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ClusterOverviewViewModel.IsLoading))
                signals++;
        };

        reload().GetAwaiter().GetResult();
        return signals;
    }

    [Fact]
    public void The_overview_does_not_flash_a_spinner_on_every_watch_event()
    {
        var page = new ClusterOverviewViewModel(new FakeClusterEngine());

        // The constructor's own load is the first one, so anything the reload announces is a repeat.
        Assert.Equal(0, LoadingSignalsDuring(page, page.LoadAsync));
        Assert.False(page.IsLoading);
    }

    [Fact]
    public void The_workloads_dashboard_does_not_flash_a_spinner_on_every_watch_event()
    {
        var page = new ClusterWorkloadsDashboardViewModel(new FakeClusterEngine(), null);

        Assert.Equal(0, LoadingSignalsDuring(page, page.LoadAsync));
        Assert.False(page.IsLoading);
    }

    [Fact]
    public void A_cluster_list_page_does_not_flash_a_spinner_on_every_watch_event()
    {
        // The same rule on the base class every other cluster list inherits (KON-319) — untested until
        // now, and it is the one the other two were written to match.
        var page = new ClusterConfigMapsViewModel(new FakeClusterEngine(), null);

        Assert.True(page.HasLoaded);
        Assert.Equal(0, LoadingSignalsDuring(page, page.LoadAsync));
        Assert.False(page.IsLoading);
    }
}
