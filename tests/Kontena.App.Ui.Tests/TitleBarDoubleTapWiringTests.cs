using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kontena.App.ViewModels;
using Kontena.App.Views;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// That the double-click on the title bar is actually wired to the window (KON-195).
/// <para>
/// <c>TitleBarDoubleTapTests</c> covers the rule; this covers the connection, which is the part that
/// was missing for three releases: the title bar's <c>ElementRole</c> was assumed to bring
/// double-click with it, nobody checked, and dragging working made it look as though it had.
/// </para>
/// <para>
/// The gesture is raised on the element rather than synthesised from two clicks: click-count detection
/// belongs to the platform, and a headless double-click would be testing Avalonia's timing rather than
/// this window's handler.
/// </para>
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class TitleBarDoubleTapWiringTests(HeadlessSessionFixture headless)
{
    private static (Window Window, Border TitleBar, StackPanel Caption) Build()
    {
        var window = new MainWindow { DataContext = new MainWindowViewModel() };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Found the way a user finds it: the strip carrying the caption buttons.
        var caption = window.GetVisualDescendants().OfType<StackPanel>()
            .First(p => p.Name == "CaptionButtons");
        var titleBar = caption.GetVisualAncestors().OfType<Border>()
            .First(b => Avalonia.Controls.Chrome.WindowDecorationProperties.GetElementRole(b)
                == WindowDecorationsElementRole.TitleBar);

        return (window, titleBar, caption);
    }

    private static void DoubleTap(Interactive on) =>
        on.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, null!));

    private static void Settle()
    {
        // The handler defers a turn so a platform that handles this itself goes first.
        for (var i = 0; i < 3; i++)
            Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public Task The_app_palette_is_loaded_so_this_is_the_real_window() =>
        headless.Session.Dispatch(
            () =>
            {
                // Guards the fixture itself: a stand-in application without Kontena's resources would
                // make every test below pass against a window that is not the shipped one.
                Assert.True(Application.Current!.TryFindResource("Console", ThemeVariant.Dark, out _));
                Assert.True(Application.Current!.TryFindResource("Console", ThemeVariant.Light, out _));
            },
            CancellationToken.None);

    [Fact]
    public Task Double_clicking_the_title_bar_maximises_and_restores() =>
        headless.Session.Dispatch(
            () =>
            {
                var (window, titleBar, _) = Build();
                Assert.Equal(WindowState.Normal, window.WindowState);

                DoubleTap(titleBar);
                Settle();
                Assert.Equal(WindowState.Maximized, window.WindowState);

                DoubleTap(titleBar);
                Settle();
                Assert.Equal(WindowState.Normal, window.WindowState);
            },
            CancellationToken.None);

    [Fact]
    public Task Double_clicking_a_caption_button_leaves_the_window_alone() =>
        headless.Session.Dispatch(
            () =>
            {
                // Two quick clicks on Minimise would otherwise minimise the window and maximise it on
                // the way out: the buttons live inside the title bar.
                var (window, _, caption) = Build();

                DoubleTap(caption.Children[0]);
                Settle();

                Assert.NotEqual(WindowState.Maximized, window.WindowState);
            },
            CancellationToken.None);
}
