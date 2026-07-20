using Avalonia;
using Avalonia.Controls;
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

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        CaptureNormal();

        var settings = _store.Load() with
        {
            WindowWidth = _normalWidth,
            WindowHeight = _normalHeight,
            WindowX = _normalX,
            WindowY = _normalY,
            WindowMaximized = WindowState == WindowState.Maximized,
        };

        _store.Save(settings);
    }
}
