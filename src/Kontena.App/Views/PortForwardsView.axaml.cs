using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace Kontena.App.Views;

public partial class PortForwardsView : UserControl
{
    public PortForwardsView() => InitializeComponent();

    /// <summary>Copy the forward's local address. The clipboard hangs off the window rather than the view
    /// model, so this stays in the view instead of dragging a TopLevel into the VM. Avalonia 12 sets a
    /// value per data format; there is no SetTextAsync any more.</summary>
    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string address } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetValueAsync(DataFormat.Text, address);
    }
}
