using Avalonia.Controls;

namespace Kontena.App.Views;

/// <summary>
/// What a double-click on the title bar should do (KON-195).
/// <para>
/// Pulled out of the handler because the interesting part is not the toggle, it is the case where
/// somebody else already did it. <c>ExtendClientAreaToDecorationsHint</c> means the window manager
/// never sees a click on non-client area, so on this machine the platform does nothing and the app has
/// to. Whether that holds on Windows and macOS is not something this repository can answer, and a
/// handler that toggles unconditionally would undo the platform's own toggle there — a double-click
/// that visibly does nothing, which is the bug it was meant to fix.
/// </para>
/// </summary>
internal static class TitleBarDoubleTap
{
    /// <summary>
    /// The state to move to, or null to leave the window alone.
    /// </summary>
    /// <param name="atTap">The state when the double-click arrived.</param>
    /// <param name="now">
    /// The state a moment later. Different from <paramref name="atTap"/> means the platform handled
    /// the click itself, and the answer is to do nothing rather than to toggle it back.
    /// </param>
    public static WindowState? Resolve(WindowState atTap, WindowState now)
    {
        if (now != atTap)
            return null;

        return atTap switch
        {
            WindowState.Maximized => WindowState.Normal,

            // Minimised cannot be double-clicked — there is no title bar on screen to hit — and
            // full screen is a mode the window never enters. Both are listed rather than folded into
            // the default so that adding a full-screen mode has to come past this.
            WindowState.Minimized or WindowState.FullScreen => null,

            _ => WindowState.Maximized,
        };
    }
}
