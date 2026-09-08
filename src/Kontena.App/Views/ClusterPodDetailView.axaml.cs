using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace Kontena.App.Views;

// Tail-following lives on the list itself — see Behaviors/AutoScroll.cs (KON-165).
public partial class ClusterPodDetailView : UserControl
{
    public ClusterPodDetailView() => InitializeComponent();

    /// <summary>Copy the internal hostname. The clipboard hangs off the window rather than the view
    /// model, so this stays in the view instead of dragging a TopLevel into the VM.</summary>
    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hostname } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetValueAsync(DataFormat.Text, hostname);
    }
}
