using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Kontena.App.ViewModels;

namespace Kontena.App.Views;

/// <summary>
/// A detail in a window of its own (KON-308), the second-window pattern TerminalWindow (KON-217)
/// established: the app's own chrome, and the same view model the drawer would have shown — nothing
/// reloaded, nothing rebuilt.
/// </summary>
public partial class DetailWindow : Window
{
    /// <summary>What this window is about, as <see cref="IDetachableDetail.DetailKey"/> — a container
    /// id, or kind/name(/namespace) — so a caller can recognise "the same item" before opening a
    /// second window for it (KON-308). A key rather than the domain object itself: every list reload
    /// builds new records for the same object, so reference identity would never match twice.</summary>
    public string Key { get; } = string.Empty;

    public DetailWindow() => InitializeComponent();

    public DetailWindow(ViewModelBase detail, string label, string key) : this()
    {
        DataContext = detail;
        Title = label;
        Key = key;
    }

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// The window owns what it shows once it has been handed over (KON-308, MainWindowViewModel's
    /// DetachDetailForWindow) — closing the window is what ends the detail's stream, same as the
    /// drawer's own close.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        (DataContext as IDisposable)?.Dispose();
    }
}
