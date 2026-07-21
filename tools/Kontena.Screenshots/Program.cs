using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Kontena.App;
using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.App.Views;
using Kontena.Core.Models;
using Kontena.Engines;
using Kontena.Engines.Fakes;
using HostApp = Kontena.App.App;

namespace Kontena.Screenshots;

// Renders a scene of the real Kontena app to a PNG using Avalonia's headless + Skia backend — no
// display, no real container engine. The built-in in-memory FakeEngine supplies the demo data
// (the same seed the app ships for dev/demo), so every pixel comes from the real views and styles.
//
// Usage:
//   dotnet run --project tools/Kontena.Screenshots -- --scene containers --theme dark --out shots/containers.png [--size 1180x760]
//
// Scenes: containers (list, hero), detail (container logs), inspect (container inspect tab),
//         images, volumes, networks, projects, run (Run-container modal), settings.
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var opts = Options.Parse(args);

        // Isolate every on-disk store into a throwaway config dir so a capture never reads or writes
        // the user's real profile. The settings store resolves its root from SpecialFolder.Application-
        // Data, which on Unix is $XDG_CONFIG_HOME (or $HOME/.config) — so pointing that at a temp dir,
        // before any store is constructed, isolates it without touching store code.
        var sandbox = Path.Combine(Path.GetTempPath(), "kontena-shots-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", sandbox);
        Environment.SetEnvironmentVariable("APPDATA", sandbox);
        // The app's ConnectPreferred honours this to boot straight into the demo engine.
        Environment.SetEnvironmentVariable("KONTENA_SCREENSHOT", "1");

        try
        {
            AppBuilder.Configure<HostApp>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithoutStarting();

            var theme = opts.Theme.Equals("light", StringComparison.OrdinalIgnoreCase)
                ? ThemePreference.Light
                : ThemePreference.Dark;
            ThemeApplier.Apply(theme);

            var settings = new KontenaSettings
            {
                Theme = theme,
                Onboarded = true,
                DefaultEngine = "docker",
                AutoDetectEngines = true,
            };
            // Present the built-in demo seed under Docker's name/chip — the shots read as a real
            // Docker session (the app itself always keeps the honest "Fake engine" identity).
            var registry = new BackendRegistry(
            [
                new FakeEngineProvider("docker", "Docker", "D"),
                new FakeClusterProvider("prod-eu-west", "GKE"),
                new FakeClusterProvider("staging", "EKS"),
                new FakeClusterProvider("minikube", "MK"),
            ]);
            var viewModel = new MainWindowViewModel(registry, new SettingsStore(), settings);

            var window = new MainWindow
            {
                DataContext = viewModel,
                Width = opts.Width,
                Height = opts.Height,
            };
            window.Show();

            // Let the fire-and-forget InitAsync → ConnectPreferred → ActivateAsync settle so the
            // container list (and its live log/stat streams) is populated before we drive the scene.
            SettleUntil(() => viewModel.IsReady, maxRounds: 120);

            ApplyScene(opts.Scene, viewModel);
            Settle(rounds: 40);

            var frame = window.CaptureRenderedFrame();
            if (frame is null)
            {
                Console.Error.WriteLine("Capture returned no frame.");
                return 1;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(opts.Out))!);
            frame.Save(opts.Out);
            Console.WriteLine($"Wrote {opts.Out} ({frame.PixelSize.Width}x{frame.PixelSize.Height}, scene '{opts.Scene}', {theme})");
            return 0;
        }
        finally
        {
            try { Directory.Delete(sandbox, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    private static void ApplyScene(string scene, MainWindowViewModel vm)
    {
        switch (scene)
        {
            case "containers":
                break; // default page

            case "images":
            case "volumes":
            case "networks":
            case "projects":
                vm.NavigateCommand.Execute(scene);
                break;

            case "settings":
                vm.ShowSettingsCommand.Execute(null);
                break;

            case "cluster":
            case "cluster-nodes":
            case "cluster-namespaces":
            case "cluster-workloads":
            case "cluster-pods":
            case "cluster-services":
                // Switch to the fake cluster → the whole UI enters cluster mode.
                vm.SwitchEngineCommand.Execute("kubernetes:prod-eu-west");
                SettleUntil(() => vm.IsClusterMode, maxRounds: 120);
                if (scene.StartsWith("cluster-", StringComparison.Ordinal))
                {
                    vm.NavigateCommand.Execute(scene["cluster-".Length..]);
                    Settle(rounds: 30);
                }
                break;

            case "run":
                vm.Containers?.RunContainerCommand.Execute(null);
                break;

            case "detail":
            case "inspect":
                OpenDetail(vm, tab: scene == "inspect" ? "inspect" : "logs");
                break;

            default:
                Console.Error.WriteLine($"Unknown scene '{scene}' — rendering containers list.");
                break;
        }
    }

    // Open the container-detail page on the first running container, exactly as clicking its row does,
    // then select the requested tab.
    private static void OpenDetail(MainWindowViewModel vm, string tab)
    {
        var row = vm.Containers?.Items.FirstOrDefault(c => c.IsRunning)
                  ?? vm.Containers?.Items.FirstOrDefault();
        row?.OpenCommand.Execute(null);
        Settle(rounds: 30); // let inspect/logs load

        if (vm.CurrentPage is ContainerDetailViewModel detail)
            detail.SelectTabCommand.Execute(tab);
    }

    // Headless has no render loop, so pump the dispatcher in rounds — draining posted continuations
    // from async load/stream work between short sleeps — until the UI has settled.
    internal static void Settle(int rounds = 40, int millisPerRound = 25)
    {
        for (var i = 0; i < rounds; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(millisPerRound);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static void SettleUntil(Func<bool> ready, int maxRounds, int millisPerRound = 25)
    {
        for (var i = 0; i < maxRounds; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (ready())
                return;
            Thread.Sleep(millisPerRound);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private sealed record Options(string Scene, string Theme, string Out, int Width, int Height)
    {
        public static Options Parse(string[] args)
        {
            string scene = "containers", theme = "dark", @out = "screenshot.png";
            int width = 1180, height = 760;

            for (var i = 0; i < args.Length - 1; i++)
            {
                switch (args[i])
                {
                    case "--scene": scene = args[++i]; break;
                    case "--theme": theme = args[++i]; break;
                    case "--out": @out = args[++i]; break;
                    case "--size":
                        var wh = args[++i].Split('x', 'X');
                        if (wh.Length == 2 && int.TryParse(wh[0], out var w) && int.TryParse(wh[1], out var h))
                        {
                            width = w;
                            height = h;
                        }
                        break;
                }
            }

            return new Options(scene, theme, @out, width, height);
        }
    }
}
