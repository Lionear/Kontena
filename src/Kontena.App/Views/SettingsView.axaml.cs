using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Kontena.App.ViewModels;

namespace Kontena.App.Views;

public partial class SettingsView : UserControl
{
    /// <summary>How long a revealed remote row stays tinted (KON-264) — long enough to catch the eye
    /// after the scroll, short enough not to read as a selection.</summary>
    private static readonly TimeSpan RevealHighlight = TimeSpan.FromSeconds(1.6);

    private SettingsViewModel? _subscribed;

    public SettingsView()
    {
        InitializeComponent();

        // Recording a shortcut (KON-180). Tunnelled, because the combination being recorded is often
        // one something else answers — Enter activates the button under the cursor, and the window's
        // own bindings are exactly the keys on offer here.
        AddHandler(KeyDownEvent, OnKeyDownPreview, RoutingStrategies.Tunnel);

        // Scrolling to a row is the view's job, not the view model's — it needs the container, which
        // only exists here. The page is rebuilt (and its view model replaced) on every settings
        // change, so the old one is unsubscribed rather than left holding this view alive.
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribed is { } previous)
            previous.RevealRemoteRequested -= OnRevealRemote;

        _subscribed = DataContext as SettingsViewModel;

        if (_subscribed is { } current)
            current.RevealRemoteRequested += OnRevealRemote;
    }

    /// <summary>
    /// Take the page to a remote's own row and mark it briefly (KON-264). The row two screens down is
    /// where its Edit and Remove live, and someone who clicked "Manage below" needs to arrive there
    /// knowing which one of them it was.
    /// </summary>
    private void OnRevealRemote(object? sender, RemoteEngineRow row)
    {
        if (DataContext is not SettingsViewModel vm)
            return;

        var index = vm.RemoteEngines.IndexOf(row);
        if (index < 0 || RemoteRows.ContainerFromIndex(index) is not { } container)
            return;

        container.BringIntoView();

        // Removed on a timer rather than on the next click: the point is that it fades by itself, so
        // nothing is left looking selected. One-shot, and it stops itself.
        container.Classes.Add("revealed");

        var timer = new DispatcherTimer { Interval = RevealHighlight };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            container.Classes.Remove("revealed");
        };
        timer.Start();
    }

    /// <summary>Picks the private key to authenticate with (KON-261).</summary>
    private async void OnBrowseKeyFile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm)
            return;

        if (await SshKeyPicker.PickAsync(TopLevel.GetTopLevel(this)) is { } path)
            vm.RemoteKeyFile = path;
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
