using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Kontena.Adapters.Docker;
using Kontena.App.ViewModels;
using Kontena.App.Views;
using Kontena.Engines;
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
            // Provider-based: the registry discovers backends (and, later, plugins).
            var registry = new EngineRegistry(
            [
                new DockerEngineProvider(),
                new PodmanEngineProvider(),
                new FakeEngineProvider(),
            ]);

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(registry),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}