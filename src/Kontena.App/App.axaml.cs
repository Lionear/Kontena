using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Kontena.Adapters.Docker;
using Kontena.App.ViewModels;
using Kontena.App.Views;
using Kontena.Engines.Fakes;

namespace Kontena.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Try real Docker first; fall back to the in-memory FakeEngine.
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(new DockerEngine(), new FakeEngine()),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}