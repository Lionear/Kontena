using System.Collections.Specialized;
using Kontena.App.Behaviors;

namespace Kontena.App.Tests;

/// <summary>
/// When a log view follows its tail (KON-165). These are the rules that were wrong, five times over:
/// each of the five log surfaces carried its own copy of "scroll on Add", and no two of them had
/// drifted the same way.
/// </summary>
public sealed class LogTailTests
{
    [Fact]
    public void A_new_line_pulls_the_view_along()
    {
        Assert.True(LogTail.ShouldTail(NotifyCollectionChangedAction.Add, following: true, count: 3));
    }

    [Fact]
    public void Refilling_the_list_counts_as_well()
    {
        // The bug. Switching container, switching tab or reloading empties and refills the list, which
        // raises one Reset and no Add at all — so a handler that only answers Add leaves you looking at
        // the old scroll position over entirely new content.
        Assert.True(LogTail.ShouldTail(NotifyCollectionChangedAction.Reset, following: true, count: 3));
    }

    [Fact]
    public void Nothing_happens_while_follow_is_off()
    {
        Assert.False(LogTail.ShouldTail(NotifyCollectionChangedAction.Add, following: false, count: 3));
        Assert.False(LogTail.ShouldTail(NotifyCollectionChangedAction.Reset, following: false, count: 3));
    }

    [Fact]
    public void An_emptied_list_has_nothing_to_scroll_to()
    {
        // Clearing raises a Reset too, and ScrollIntoView(-1) is not a position.
        Assert.False(LogTail.ShouldTail(NotifyCollectionChangedAction.Reset, following: true, count: 0));
    }

    [Theory]
    [InlineData(NotifyCollectionChangedAction.Remove)]
    [InlineData(NotifyCollectionChangedAction.Replace)]
    [InlineData(NotifyCollectionChangedAction.Move)]
    public void Changes_that_add_no_tail_leave_the_view_alone(NotifyCollectionChangedAction action)
    {
        Assert.False(LogTail.ShouldTail(action, following: true, count: 3));
    }

    [Fact]
    public void Scrolling_up_stops_the_view_following()
    {
        // The complaint underneath the ticket: reading something back and being yanked to the tail
        // mid-sentence.
        var following = LogTail.FollowAfterScroll(
            following: true, programmatic: false, offset: 0, extent: 1000, viewport: 200);

        Assert.False(following);
    }

    [Fact]
    public void Scrolling_back_to_the_bottom_resumes()
    {
        var following = LogTail.FollowAfterScroll(
            following: false, programmatic: false, offset: 800, extent: 1000, viewport: 200);

        Assert.True(following);
    }

    [Fact]
    public void Our_own_scroll_is_not_the_user_deciding_anything()
    {
        // Without this the tail-follow reads as a user scroll, and the first line after switching
        // Follow on would switch it straight back off.
        var following = LogTail.FollowAfterScroll(
            following: true, programmatic: true, offset: 0, extent: 1000, viewport: 200);

        Assert.True(following);
    }

    [Fact]
    public void A_list_that_has_just_been_given_room_jumps_to_its_end()
    {
        // Behind a tab, every line arrives before the list has ever been laid out (KON-198).
        Assert.True(LogTail.ShouldTailOnAppearing(hadRoom: false, hasRoom: true, following: true, count: 200));
    }

    [Fact]
    public void Room_it_already_had_is_not_a_moment()
    {
        // Otherwise every resize — a window drag, a splitter — would yank a reader back to the tail.
        Assert.False(LogTail.ShouldTailOnAppearing(hadRoom: true, hasRoom: true, following: true, count: 200));
        Assert.False(LogTail.ShouldTailOnAppearing(hadRoom: false, hasRoom: false, following: true, count: 200));
    }

    [Fact]
    public void Appearing_with_follow_off_or_nothing_to_show_changes_nothing()
    {
        Assert.False(LogTail.ShouldTailOnAppearing(hadRoom: false, hasRoom: true, following: false, count: 200));
        Assert.False(LogTail.ShouldTailOnAppearing(hadRoom: false, hasRoom: true, following: true, count: 0));
    }

    [Fact]
    public void A_list_shorter_than_its_viewport_is_at_the_bottom()
    {
        // Nothing to scroll, so nothing about it means "I have scrolled away".
        Assert.True(LogTail.IsAtBottom(offset: 0, extent: 120, viewport: 400));
    }

    [Fact]
    public void A_rounding_difference_is_not_a_decision()
    {
        // Landing a few pixels short of the extent happens on its own; treating that as scrolling away
        // would switch Follow off during normal streaming.
        Assert.True(LogTail.IsAtBottom(offset: 795, extent: 1000, viewport: 200));
        Assert.False(LogTail.IsAtBottom(offset: 700, extent: 1000, viewport: 200));
    }
}
