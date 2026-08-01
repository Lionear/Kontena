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
    /// <summary>The domain object this window is about — a Node, KubeNamespace, Pod, Workload,
    /// Service, or ContainerSummary — so a caller can recognise "the same item" by reference before
    /// opening a second window for it (KON-308).</summary>
    public object? Target { get; }

    public DetailWindow() => InitializeComponent();

    public DetailWindow(ViewModelBase detail, string label, object? target) : this()
    {
        DataContext = detail;
        Title = label;
        Target = target;
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
