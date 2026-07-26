using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Kontena.Adapters.Docker;
using Kontena.Adapters.Kubernetes;
using Kontena.Adapters.Podman;
using Kontena.App.Services;
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
            var store = new SettingsStore();
            var settings = store.Load();
            ThemeApplier.Apply(settings.Theme);
            DensityApplier.Apply(settings.CompactDensity);

            // Provider-based: the registry discovers backends (and, later, plugins). BackendCatalog
            // owns the composition so the runtime demo toggle (KON-96) builds the same list.
            var registry = new BackendRegistry(
                BackendCatalog.Build(
                    BackendCatalog.ShouldIncludeDemo(settings.ShowDemoBackends), settings.RemoteEngines));

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(registry, store, settings),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}