using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Kontena.App.ViewModels;

namespace Kontena.App.Views;

public partial class ClusterTerminalsView : UserControl
{
    public ClusterTerminalsView() => InitializeComponent();

    /// <summary>
    /// Move the selected terminal into a window of its own (KON-217). Done here rather than in the view
    /// model for the same reason the clipboard is: a window belongs to the UI, and routing it through a
    /// view model would be a longer way round to the same <c>Show()</c>.
    /// </summary>
    private void OnDetachClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ClusterTerminalsViewModel { Shown: { } terminal })
            new TerminalWindow(terminal).Show();
    }

    /// <summary>
    /// Bring it back by closing its window, so this button and the window's own close run the same path
    /// — the terminal returns because the window closed, not because a particular button was pressed.
    /// </summary>
    private void OnReattachClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ClusterTerminalsViewModel { Selected: { } selected })
            WindowFor(selected)?.Close();
    }

    /// <summary>
    /// Found by the terminal it is showing rather than by a handle kept here: this page is rebuilt on
    /// every visit and the window outlives it, so a page that never opened one still has to close it.
    /// </summary>
    private static TerminalWindow? WindowFor(ClusterTerminalViewModel terminal) =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows
            .OfType<TerminalWindow>()
            .FirstOrDefault(w => w.DataContext is ClusterTerminalViewModel other
                                 && ReferenceEquals(other.Terminal, terminal.Terminal));
}
