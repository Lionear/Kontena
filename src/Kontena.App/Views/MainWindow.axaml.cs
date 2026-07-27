using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Kontena.App.Services;

namespace Kontena.App.Views;

public partial class MainWindow : Window
{
    private readonly SettingsStore _store = new();

    // Last known "normal" (non-maximized) placement, so restoring un-maximizes to a sane size.
    private double? _normalWidth;
    private double? _normalHeight;
    private int? _normalX;
    private int? _normalY;

    public MainWindow()
    {
        InitializeComponent();
        RestorePlacement();

        SizeChanged += (_, _) => CaptureNormal();
        PositionChanged += (_, _) => CaptureNormal();
        Closing += OnClosing;
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

    // Our own title bar has to do the two things the system's used to do for free (KON-134): drag
    // the window, and toggle maximize on a double click. The caption buttons are still the system's
    // and sit above this in the window template, so they take their own clicks first.
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            e.Handled = true;
            return;
        }

        BeginMoveDrag(e);
    }

    // Picking a backend (or the "add…" row) should dismiss the switcher flyout — a Button click
    // inside a Flyout doesn't close it on its own. Defer the close to the next dispatcher tick so
    // the button's Command (the actual switch) runs first; hiding synchronously here cancels the
    // click before the command executes.
    private void OnSwitcherItemClick(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(() => BackendPill.Flyout?.Hide());

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
