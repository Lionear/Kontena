using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace Kontena.App.Views;

public partial class OnboardingView : UserControl
{
    public OnboardingView() => InitializeComponent();

    /// <summary>
    /// Copies the suggested fix, so someone who would rather run it themselves can. The command
    /// manages a unit on the user's own machine — offering only "let Kontena do it" would be the
    /// wrong sole option there (KON-335).
    /// </summary>
    private async void OnCopyFixCommandClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string command } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetValueAsync(DataFormat.Text, command);
    }
}
