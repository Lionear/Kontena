using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.Core.Orchestration;

namespace Kontena.App.Views;

public partial class MainWindow : Window
{
    private readonly SettingsStore _store = new();

    private void OnPointerPressedPreview(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsXButton1Pressed)
            return;

        if (DataContext is ViewModels.MainWindowViewModel { CanGoBack: true } vm)
        {
            vm.GoBackCommand.Execute(null);
            e.Handled = true;
        }
    }

    // Last known "normal" (non-maximized) placement, so restoring un-maximizes to a sane size.
    private double? _normalWidth;
    private double? _normalHeight;
    private int? _normalX;
    private int? _normalY;

    public MainWindow()
    {
        InitializeComponent();
        RestorePlacement();

        // The mouse back button, which every browser and file manager answers and nothing in Kontena
        // did (KON-173). Tunnelled so a list row cannot swallow it on the way up.
        AddHandler(PointerPressedEvent, OnPointerPressedPreview, RoutingStrategies.Tunnel);

        // Focus belongs to the view, so the shell asks rather than reaching into the tree (KON-172).
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ViewModels.MainWindowViewModel vm)
            {
                vm.RequestFocusSearch = () => SearchBox.Focus();
                vm.RequestRebindShortcuts = RebindShortcuts;
            }

            RebindShortcuts();
        };

        RebindShortcuts();

        SizeChanged += (_, _) => CaptureNormal();
        PositionChanged += (_, _) => CaptureNormal();
        Closing += OnClosing;

        SyncMaximiseButton();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
                SyncMaximiseButton();
        };
    }

    /// <summary>
    /// Fill <see cref="InputElement.KeyBindings"/> from the action registry and the gestures the user
    /// changed (KON-180). Called again whenever Settings changes one, which is what "without a restart"
    /// means: the collection is rebuilt rather than the window recreated.
    /// </summary>
    private void RebindShortcuts()
    {
        KeyBindings.Clear();

        if (DataContext is not ViewModels.MainWindowViewModel vm)
            return;

        var configured = _store.Load().Shortcuts;

        foreach (var (action, gesture) in ShellActions.Resolve(configured))
        {
            if (!vm.ShortcutCommands.TryGetValue(action.Id, out var command))
                continue;

            // Resolve already discards anything unparseable, so this is belt and braces against a
            // hand-edited settings file: one bad line must not cost the other shortcuts.
            KeyGesture parsed;
            try
            {
                parsed = KeyGesture.Parse(gesture);
            }
            catch (ArgumentException)
            {
                continue;
            }
            catch (FormatException)
            {
                continue;
            }

            KeyBindings.Add(new KeyBinding { Gesture = parsed, Command = command });
        }
    }

    private void RestorePlacement()
    {
        var settings = _store.Load();

        if (settings.WindowWidth is >= 400 && settings.WindowHeight is >= 300)
        {
            Width = settings.WindowWidth.Value;
            Height = settings.WindowHeight.Value;
        }

        if (settings.WindowX is { } x && settings.WindowY is { } y)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(x, y);
        }

        if (settings.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    private void CaptureNormal()
    {
        if (WindowState != WindowState.Normal)
            return;

        if (!double.IsNaN(Width) && Width > 0 && !double.IsNaN(Height) && Height > 0)
        {
            _normalWidth = Width;
            _normalHeight = Height;
        }

        _normalX = Position.X;
        _normalY = Position.Y;
    }

    /// <summary>
    /// Maximise or restore on a double-click, the way every other window on the system does (KON-195).
    /// <para>
    /// This used to be left to the platform, on the strength of the title bar's <c>ElementRole</c>.
    /// Dragging does come from there; double-click does not, because with the client area extended the
    /// window manager never sees a click on non-client area at all.
    /// </para>
    /// <para>
    /// Deferred one dispatcher turn so a platform that <i>does</i> handle it gets to go first: see
    /// <see cref="TitleBarDoubleTap"/> for why toggling unconditionally would be worse than not
    /// handling it.
    /// </para>
    /// </summary>
    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e)
    {
        // The caption buttons live inside the title bar, so two quick clicks on Minimise would
        // otherwise minimise and then maximise the window on the way out.
        if (e.Source is Visual source
            && (ReferenceEquals(source, CaptionButtons) || source.GetVisualAncestors().Contains(CaptionButtons)))
        {
            return;
        }

        var atTap = WindowState;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (TitleBarDoubleTap.Resolve(atTap, WindowState) is { } next)
                    WindowState = next;
            },
            DispatcherPriority.Background);
    }

    // Caption buttons (KON-138). Dragging is the platform's, through the title bar's ElementRole;
    // these three are ours because handing them to the platform is what makes the same window behave
    // differently on each one.
    private void OnMinimiseClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximiseClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Keeps the maximise button saying what it will do rather than what the window is.</summary>
    private void SyncMaximiseButton()
    {
        var maximised = WindowState == WindowState.Maximized;

        MaximiseGlyph.Data = (Geometry?)this.FindResource(
            maximised ? "IconWindowRestore" : "IconWindowMaximize");
        ToolTip.SetTip(MaximiseButton, maximised ? "Restore" : "Maximise");
    }

    // Picking a backend (or the "add…" row) should dismiss the switcher flyout — a Button click
    // inside a Flyout doesn't close it on its own. Defer the close to the next dispatcher tick so
    // the button's Command (the actual switch) runs first; hiding synchronously here cancels the
    // click before the command executes.
    private void OnSwitcherItemClick(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(() => BackendPill.Flyout?.Hide());

    // The drawer grows leftwards, so a drag towards the left — a negative X — widens it (KON-307).
    // The clamp and the saving live in the view model; this only turns a gesture into a delta.
    private void OnDetailResize(object? sender, VectorEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.ResizeDetail(-e.Vector.X);
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        CaptureNormal();

        _store.Update(s => s with
        {
            WindowWidth = _normalWidth,
            WindowHeight = _normalHeight,
            WindowX = _normalX,
            WindowY = _normalY,
            WindowMaximized = WindowState == WindowState.Maximized,
        });
    }
}
