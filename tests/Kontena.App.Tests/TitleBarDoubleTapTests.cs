using Avalonia.Controls;
using Kontena.App.Views;

namespace Kontena.App.Tests;

/// <summary>
/// What a double-click on the title bar does (KON-195).
/// <para>
/// The toggle is the easy half. The half worth pinning is the case where the platform already handled
/// the click: on this machine it does not — with the client area extended the window manager never sees
/// non-client area — but that cannot be verified for Windows and macOS from here, and a handler that
/// toggles regardless would undo the platform's own toggle. The result would be a double-click that
/// visibly does nothing, which is exactly the bug being fixed.
/// </para>
/// </summary>
public sealed class TitleBarDoubleTapTests
{
    [Fact]
    public void A_normal_window_maximises()
    {
        Assert.Equal(
            WindowState.Maximized,
            TitleBarDoubleTap.Resolve(WindowState.Normal, WindowState.Normal));
    }

    [Fact]
    public void A_maximised_window_goes_back_to_its_previous_size()
    {
        // Which size that is comes from CaptureNormal, which already tracked it for restoring the
        // placement on the next launch. Nothing new to remember here.
        Assert.Equal(
            WindowState.Normal,
            TitleBarDoubleTap.Resolve(WindowState.Maximized, WindowState.Maximized));
    }

    [Theory]
    [InlineData(WindowState.Normal, WindowState.Maximized)]
    [InlineData(WindowState.Maximized, WindowState.Normal)]
    public void A_platform_that_already_toggled_is_left_alone(WindowState atTap, WindowState now)
    {
        // The whole point of the guard: two toggles net out to nothing, and nothing is what the user
        // reported in the first place.
        Assert.Null(TitleBarDoubleTap.Resolve(atTap, now));
    }

    [Theory]
    [InlineData(WindowState.Minimized)]
    [InlineData(WindowState.FullScreen)]
    public void States_with_no_title_bar_to_click_do_nothing(WindowState state)
    {
        Assert.Null(TitleBarDoubleTap.Resolve(state, state));
    }
}
