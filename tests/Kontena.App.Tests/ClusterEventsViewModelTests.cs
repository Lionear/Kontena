using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// The cluster-wide events feed (KON-248). Until this page existed, ListEventsAsync was only ever
/// called from pod detail and object detail — so an event could only be found once its object had
/// been, which is the wrong way round for the question the feed answers.
/// </summary>
public sealed class ClusterEventsViewModelTests
{
    private static ClusterEvent Event(
        string reason, EventSeverity severity = EventSeverity.Normal, string kind = "Pod",
        string name = "web-7f9", int count = 1, int minutesAgo = 1, string message = "something happened") =>
        new()
        {
            Reason = reason,
            Message = message,
            Severity = severity,
            Count = count,
            LastSeen = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
            InvolvedObject = new ResourceRef(new GroupVersionKind(string.Empty, "v1", kind), "app", name),
        };

    private static ClusterEventRow Row(ClusterEvent e, Func<ResourceRef, Task>? open = null) => new(e, open);

    [Fact]
    public void An_object_with_a_detail_page_is_a_link_and_everything_else_is_not()
    {
        // A link that opens nothing is worse than plain text: it promises a route that is not there.
        Assert.True(Row(Event("BackOff"), _ => Task.CompletedTask).CanOpen);
        Assert.True(Row(Event("ScalingReplicaSet", kind: "Deployment"), _ => Task.CompletedTask).CanOpen);
        Assert.False(Row(Event("FailedMount", kind: "PersistentVolumeClaim"), _ => Task.CompletedTask).CanOpen);

        // And no handler means no link at all, whatever the kind.
        Assert.False(Row(Event("BackOff")).CanOpen);
    }

    [Fact]
    public void The_object_cell_names_the_kind_as_well_as_the_object()
    {
        // "web-7f9" alone does not say whether the event is about the pod or the deployment that made
        // it, and both fire in the same feed within seconds of each other.
        Assert.Equal("Pod/web-7f9", Row(Event("BackOff")).ObjectName);
        Assert.Equal("in app", Row(Event("BackOff")).ObjectTooltip);
    }

    [Fact]
    public void A_repeated_event_is_marked_and_a_single_one_is_not()
    {
        // 340 restarts and one bad start are different facts, and the count is what tells them apart.
        var repeated = Row(Event("BackOff", count: 340));
        Assert.True(repeated.IsRepeating);
        Assert.Equal("×340", repeated.CountText);

        var once = Row(Event("Scheduled"));
        Assert.False(once.IsRepeating);
        Assert.Equal(string.Empty, once.CountText);
    }

    // ── The page ────────────────────────────────────────────────────────────

    private static async Task<ClusterEventsViewModel> PageAsync(
        Func<ResourceRef, Task<bool>>? onOpen = null)
    {
        var page = new ClusterEventsViewModel(new FakeClusterEngine(), null, onOpen);
        await page.LoadAsync();
        return page;
    }

    [Fact]
    public async Task The_newest_event_is_at_the_top()
    {
        var page = await PageAsync();

        // The API returns events in no order worth relying on, and "what just happened" is the whole
        // reason for the page. Asserted on the rows rather than the models, because the sort is only
        // useful if it survives into what is on screen.
        var seen = page.Items.Select(r => r.LastSeenAt).ToList();

        Assert.NotEmpty(seen);
        Assert.Equal(seen.OrderByDescending(t => t), seen);
    }

    [Fact]
    public async Task Warnings_only_hides_the_normal_events_and_leaves_the_warnings()
    {
        var page = await PageAsync();

        var warnings = page.WarningCount;
        Assert.True(warnings > 0, "the fake cluster needs at least one warning for this to mean anything");
        Assert.True(page.Items.Count > warnings, "and at least one normal event alongside it");

        page.WarningsOnly = true;

        Assert.Equal(warnings, page.Items.Count);
        Assert.All(page.Items, row => Assert.True(row.IsWarning));
    }

    [Fact]
    public async Task The_toggle_and_the_search_box_both_apply_at_once()
    {
        // Include() runs whether or not anything has been typed, which is exactly the case a filter
        // built on Matches() alone would get wrong.
        var page = await PageAsync();
        page.WarningsOnly = true;

        var reason = page.Items[0].Reason;
        page.SearchText = reason;

        Assert.All(page.Items, row => Assert.True(row.IsWarning));
        Assert.All(page.Items, row => Assert.Contains(reason, row.Reason, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_object_that_is_gone_says_so_rather_than_doing_nothing()
    {
        // Events outlive their objects by design, so a crash-looping pod that has since been replaced
        // is the single most likely row to be clicked.
        var page = await PageAsync(_ => Task.FromResult(false));

        var row = page.Items.First(r => r.CanOpen);
        await row.OpenCommand.ExecuteAsync(null);

        Assert.NotNull(page.NavigationNotice);
        Assert.Contains("not there any more", page.NavigationNotice, StringComparison.Ordinal);
        Assert.Equal(page.NavigationNotice, page.Notice);
    }

    [Fact]
    public async Task Following_an_object_that_is_still_there_leaves_no_notice()
    {
        var page = await PageAsync(_ => Task.FromResult(true));

        var row = page.Items.First(r => r.CanOpen);
        await row.OpenCommand.ExecuteAsync(null);

        Assert.Null(page.NavigationNotice);
    }

    [Fact]
    public async Task An_empty_feed_is_explained_as_the_good_news_it_is()
    {
        // A quiet namespace reads as a page that failed to load unless it is told apart, and the
        // retention window is the part people get wrong.
        var page = new ClusterEventsViewModel(new FakeClusterEngine(), "kube-node-lease");
        await page.LoadAsync();

        Assert.Empty(page.Items);
        Assert.NotNull(page.Notice);
        Assert.Contains("about an hour", page.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Filtering_everything_away_is_told_apart_from_an_empty_namespace()
    {
        var page = await PageAsync();
        page.SearchText = "no-event-says-this-anywhere";

        Assert.Empty(page.Items);
        Assert.Equal("No events match that search.", page.Notice);
    }
}
