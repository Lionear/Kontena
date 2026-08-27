using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Kontena.Adapters.Docker;
using Kontena.Adapters.Kubernetes;
using Kontena.Adapters.Podman;
using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.App.Views;
using Kontena.Sdk.Models;
using Kontena.Sdk;
using Kontena.Engines.Fakes;
using Kontena.Core.Models;
using Kontena.Core.Versioning;
using Kontena.Engines;
using Kontena.Engines.Plugins;

namespace Kontena.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // What macOS puts next to the Apple logo, and in "About …", "Hide …" and "Quit …" (KON-356).
        // Avalonia builds that menu itself and reads Application.Name for it — not the bundle's
        // CFBundleName, which is why the packaging work in KON-348 left it saying "Avalonia
        // Application". Unset, that string is the property's default.
        Name = ProductInfo.Name;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Diag.Mark("framework initialised");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var store = new SettingsStore();
            var settings = store.Load();

            // Before anything else this method does, so that the marks below are the first lines of
            // the file rather than the ones that happened to come after it was ready. Opening archives
            // the previous session's log to .prev — which is the session a crash report is about
            // (KON-389).
            if (settings.DiagnosticLogging)
                DiagLog.Open();

            ThemeApplier.Apply(settings.Theme);
            DensityApplier.Apply(settings.CompactDensity);
            Diag.Mark("settings read, theme applied");

            // Provider-based: the registry discovers backends (and, later, plugins). BackendCatalog
            // owns the composition so the runtime demo toggle (KON-96) builds the same list.
            // Which clusters exist is read from files; which of them belong in the switcher is the
            // user's answer (KON-120). An installation that predates the choice keeps what it had.
            var clusters = BackendCatalog.DiscoverClusters(settings.KubeconfigPaths)
                .Select(p => p.Backend)
                .ToList();

            settings = store.Update(s => s.AdoptExistingClusters(clusters).PruneClusters(clusters));
            Diag.Mark("kubeconfigs read");

            // Before the window, because a provider a plugin contributes has to be in the very first
            // catalog — the switcher is built from that. Only what already has consent is loaded here;
            // anything new comes back as AwaitingConsent and is asked about once there is a window to
            // ask in (MainWindowViewModel.InitAsync).
            var plugins = PluginLoader.Discover(
                PluginLoader.DefaultRoot,
                c => settings.AllowsPlugin(c.Manifest.Id, c.Manifest.Version, c.Sha256));

            foreach (var plugin in plugins.Where(p => p.Manifest is not null))
                BackendCatalog.SetPluginProviders(plugin.Manifest!.Id, plugin.Providers);

            var registry = new BackendRegistry(
                BackendCatalog.Build(
                    BackendCatalog.ShouldIncludeDemo(settings.ShowDemoBackends),
                    settings.RemoteEngines, settings.KubeconfigPaths, settings.ShowsCluster,
                    settings.IsAdapterEnabled));
            Diag.Mark("plugins loaded, catalog built");

            var window = Diag.Time("build the window", () => new MainWindow());
            Diag.Time("build the shell view model",
                () => window.DataContext = new MainWindowViewModel(
                    registry, store, settings, plugins: plugins,
                    versions: new VersionSupportCheck(new EndOfLifeCalendar())));

            // Both from here, because both are about the window: when it first appeared, and every
            // stall on the thread that draws it from then on.
            window.Opened += (_, _) =>
            {
                Diag.Mark("window on screen");
                Diag.WatchUiThread();
            };

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}