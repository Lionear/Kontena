using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Kontena.Adapters.Docker;
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

            // Provider-based: the registry discovers backends (and, later, plugins).
            var providers = new List<IBackendProvider>
            {
                new DockerEngineProvider(),
                new PodmanEngineProvider(),
            };

            // The in-memory demo backend is a dev/testing aid — never shipped to users.
            // Available in Debug builds, or opt-in from a release build for demos/screenshots.
#if DEBUG
            providers.Add(new FakeEngineProvider());
#else
            if (Environment.GetEnvironmentVariable("KONTENA_FAKE_ENGINE") == "1")
                providers.Add(new FakeEngineProvider());
#endif

            var registry = new BackendRegistry(providers);

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(registry, store, settings),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}