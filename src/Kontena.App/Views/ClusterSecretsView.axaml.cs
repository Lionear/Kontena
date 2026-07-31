using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Kontena.App.ViewModels;

namespace Kontena.App.Views;

public partial class ClusterSecretsView : UserControl
{
    public ClusterSecretsView() => InitializeComponent();

    /// <summary>
    /// Copy a value without putting it on screen (KON-249).
    /// <para>
    /// Handled here rather than by a command because the value must not become a bound property to
    /// be copied: it is fetched, handed to the clipboard, and never held. Copying and revealing are
    /// separate acts, and wanting one without the other is the normal case — a password goes into a
    /// terminal far more often than onto a screen someone else can see.
    /// </para>
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
