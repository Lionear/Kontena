using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Kontena.App.ViewModels;

namespace Kontena.App.Views;

public partial class ClusterConfigMapsView : UserControl
{
    public ClusterConfigMapsView() => InitializeComponent();

    /// <summary>
    /// The same copy path as the secrets page, on purpose: one behaviour, so a config map key and a
    /// secret key are carried out of Kontena the same way.
    /// </summary>
    private async void CopyValue(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ConfigKeyRow row })
            return;

        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;

        if (await row.ForClipboardAsync() is { } value)
            await clipboard.SetValueAsync(DataFormat.Text, value);
    }
}
