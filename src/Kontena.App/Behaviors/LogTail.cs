using System.Collections.Specialized;

namespace Kontena.App.Behaviors;

/// <summary>
/// When a log view should jump to the bottom, and when following should stop (KON-165).
/// <para>
/// Pure, and separate from the control it drives, because the bug this replaces <i>was</i> a rule:
/// five views each carried their own copy of "scroll on <c>Add</c>", which silently meant "and stay
/// where you are when the list is refilled". A rule you can only exercise by running the UI is a rule
/// that drifts five ways.
/// </para>
/// </summary>
public static class LogTail
{
    /// <summary>
    /// Whether a collection change should move the view to the last line.
    /// <para>
    /// <c>Reset</c> counts. Emptying and refilling a list — switching container, switching tab,
    /// reloading — raises one <c>Reset</c> and no <c>Add</c> at all, so a handler that only answers
    /// <c>Add</c> leaves you looking at the old scroll position over entirely new content.
    /// </para>
    /// </summary>
    public static bool ShouldTail(NotifyCollectionChangedAction action, bool following, int count) =>
        following
        && count > 0
        && action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset;

    /// <summary>
    /// How close to the bottom still counts as the bottom, in device pixels. A line of log text is
    /// around 18px; anything under one line is a rounding difference, not a decision to scroll away.
    /// </summary>
    public const double BottomThreshold = 16;

    /// <summary>
    /// Whether the view is close enough to the end that new lines should keep pulling it along.
    /// </summary>
    public static bool IsAtBottom(double offset, double extent, double viewport) =>
        extent <= viewport || offset >= extent - viewport - BottomThreshold;

    /// <summary>
    /// What Follow becomes after the view has scrolled.
    /// <para>
    /// Scrolling up is how you read something back, and being yanked to the tail mid-sentence is the
    /// complaint behind this ticket. Scrolling back to the bottom resumes, which is what <c>docker
    /// logs -f</c> effectively does in a terminal and saves having to find the button again.
    /// </para>
    /// <param name="programmatic">
    /// True when this scroll is the tail-follow itself. Our own scroll must not be read as the user
    /// deciding anything, or the first new line after enabling Follow would switch it straight off.
    /// </param>
    /// </summary>
    public static bool FollowAfterScroll(
        bool following, bool programmatic, double offset, double extent, double viewport)
    {
        if (programmatic)
            return following;

        return IsAtBottom(offset, extent, viewport);
    }

    /// <summary>
    /// Whether a list that has just been given room should jump to its end (KON-198).
    /// <para>
    /// The detail pages put their logs behind a tab, and a tab here is a panel toggled with
    /// <c>IsVisible</c> rather than content built on demand. So the list is in the visual tree from
    /// the start: attaching fires immediately, with no lines yet, and every line that arrives while
    /// the tab is hidden scrolls a viewport of zero height — which does nothing. Clicking Logs then
    /// lays the list out for the first time, at offset zero. At the top, which is what was reported.
    /// </para>
    /// <para>
    /// Becoming visible is its own moment, separate from being attached, and this is the rule for it:
    /// the first time a list actually has room, put it on its last line.
    /// </para>
    /// </summary>
    public static bool ShouldTailOnAppearing(bool hadRoom, bool hasRoom, bool following, int count) =>
        following && count > 0 && hasRoom && !hadRoom;
}
