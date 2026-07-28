using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Kontena.App.ViewModels;

namespace Kontena.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();

        // Recording a shortcut (KON-180). Tunnelled, because the combination being recorded is often
        // one something else answers — Enter activates the button under the cursor, and the window's
        // own bindings are exactly the keys on offer here.
        AddHandler(KeyDownEvent, OnKeyDownPreview, RoutingStrategies.Tunnel);
    }

    private void OnKeyDownPreview(object? sender, KeyEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm)
            return;

        var row = vm.Shortcuts.FirstOrDefault(r => r.IsRecording);
        if (row is null)
            return;

        // A modifier on its own is not a combination yet — everyone presses Ctrl before the letter.
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }

        e.Handled = true;

        // Escape cancels the recorder rather than being recorded. It is the one key that already means
        // "never mind" everywhere, and it is the default for Close dialog anyway — so the way to get
        // Escape back is Restore the default, not pressing it here.
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            row.CancelRecordCommand.Execute(null);
            return;
        }

        // A refused combination leaves the row listening, so the next attempt is one keypress rather
        // than click-then-press.
        row.Offer(new KeyGesture(e.Key, e.KeyModifiers).ToString());
    }
}
