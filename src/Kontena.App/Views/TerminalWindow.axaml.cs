using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Kontena.App.ViewModels;

namespace Kontena.App.Views;

/// <summary>
/// A terminal in a window of its own (KON-217).
/// <para>
/// It holds the same view model the page would, so everything below it is unchanged — the shell is not
/// restarted or handed over, it is the same session with a different view attached. Which is also why
/// this moves rather than mirrors: one viewer at a time, or two emulators would fight over one PTY's
/// size.
/// </para>
/// </summary>
public partial class TerminalWindow : Window
{
    public TerminalWindow() => InitializeComponent();

    public TerminalWindow(ClusterTerminalViewModel terminal) : this()
    {
        DataContext = terminal;
        Title = terminal.Title + " · " + terminal.Context;

        terminal.IsDetached = true;

        // A closed tab takes its window with it. Without this the window stands there showing a shell
        // that has been torn down, and every keystroke goes nowhere.
        terminal.Terminal.Closed += Close;

        // Whichever way it closes — this button, the window manager, a keyboard shortcut — the terminal
        // goes back to the page. Doing it on Closed rather than in the click handler is what makes that
        // true for the ways we did not write.
        Closed += (_, _) => terminal.IsDetached = false;
    }

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
