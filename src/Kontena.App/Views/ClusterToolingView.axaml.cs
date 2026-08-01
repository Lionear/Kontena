using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace Kontena.App.Views;

public partial class ClusterToolingView : UserControl
{
    public ClusterToolingView() => InitializeComponent();

    /// <summary>
    /// Copy the install command. The clipboard hangs off the window rather than the view model, so
    /// this stays in the view — same as PortForwardsView.
    /// </summary>
    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string command } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetValueAsync(DataFormat.Text, command);
    }
}
