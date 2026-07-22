using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using System.Collections.Generic;
using Kontena.Adapters.Kubernetes;
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
//         images, volumes, networks, projects, run (Run-container modal), settings,
//         apply / apply-plan / apply-done (the declarative flow),
//         apply-kustomize / apply-helm (the render sources — these run the real
//         kustomize/helm CLIs over the repository's own samples, and skip nothing:
//         without the tool installed the capture shows the render's error state).
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
            var providers = new List<IBackendProvider>
            {
                new FakeEngineProvider("docker", "Docker", "D"),
                new FakeClusterProvider("prod-eu-west", "GKE"),
                new FakeClusterProvider("staging", "EKS"),
                new FakeClusterProvider("minikube", "MK"),
            };

            // Every other scene stays on the seeded fakes so shots are reproducible; only the
            // real-* scenes reach for the machine's actual kubeconfig (KON-68/86).
            if (opts.Scene.StartsWith("real-", StringComparison.Ordinal))
                providers.AddRange(KubernetesClusterProvider.DiscoverAll());

            var registry = new BackendRegistry(providers);
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

    /// <summary>
    /// A bundle picked to exercise every plan outcome against the seeded fake cluster: the "api"
    /// Deployment is scaled and re-imaged (configure), the autoscaler is new (create), and the
    /// "api" Service is written exactly as it already exists (no change).
    /// </summary>
    private const string PlanSampleManifest = """
        apiVersion: apps/v1
        kind: Deployment
        metadata:
          name: api
          namespace: app
        spec:
          replicas: 5
          selector:
            matchLabels: {app: api}
          template:
            spec:
              containers:
                - name: api
                  image: ghcr.io/lionear/api:2.0
        ---
        apiVersion: autoscaling/v2
        kind: HorizontalPodAutoscaler
        metadata:
          name: api
          namespace: app
        spec:
          minReplicas: 3
          maxReplicas: 10
        ---
        apiVersion: v1
        kind: Service
        metadata:
          name: api
          namespace: app
        spec:
          type: ClusterIP
          clusterIP: 10.0.12.4
          selector:
            app: api
          ports:
            - name: http
              port: 80
              targetPort: 8080
              protocol: TCP
        """;

    /// <summary>
    /// Dry-run sample for the live cluster: one resource that exists (kube-root-ca.crt is in every
    /// namespace) so the plan shows an unchanged row, and one that does not, so it shows a create.
    /// </summary>
    private const string LiveApplySample = """
        apiVersion: v1
        kind: ConfigMap
        metadata:
          name: kontena-demo-config
          namespace: default
        data:
          greeting: hello
        ---
        apiVersion: apps/v1
        kind: Deployment
        metadata:
          name: kontena-demo-web
          namespace: default
        spec:
          replicas: 2
          selector:
            matchLabels:
              app: kontena-demo-web
          template:
            metadata:
              labels:
                app: kontena-demo-web
            spec:
              containers:
                - name: web
                  image: nginx:1.27-alpine
        """;

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
            case "settings-engines":
                vm.ShowSettingsCommand.Execute(null);
                if (scene == "settings-engines" && vm.SettingsPage is Kontena.App.ViewModels.SettingsViewModel s)
                {
                    s.SelectCategoryCommand.Execute("engines");
                    Settle(rounds: 20);
                }

                break;

            case "cluster":
            case "cluster-nodes":
            case "cluster-namespaces":
            case "cluster-workloads":
            case "cluster-pods":
            case "cluster-services":
                // Switch to the fake cluster → the whole UI enters cluster mode.
                vm.SwitchEngineCommand.Execute("fakecluster:prod-eu-west");
                SettleUntil(() => vm.IsClusterMode, maxRounds: 120);
                if (scene.StartsWith("cluster-", StringComparison.Ordinal))
                {
                    vm.NavigateCommand.Execute(scene["cluster-".Length..]);
                    Settle(rounds: 30);
                }
                break;

            case "pod":
            case "pod-logs":
            case "pod-yaml":
                vm.SwitchEngineCommand.Execute("fakecluster:prod-eu-west");
                SettleUntil(() => vm.IsClusterMode, maxRounds: 120);
                vm.NavigateCommand.Execute("pods");
                Settle(rounds: 30);
                if (vm.CurrentPage is Kontena.App.ViewModels.ClusterPodsViewModel pods)
                {
                    pods.Pods.FirstOrDefault()?.OpenCommand.Execute(null);
                    Settle(rounds: 30);
                }
                if (vm.CurrentPage is Kontena.App.ViewModels.ClusterPodDetailViewModel detailVm)
                {
                    if (scene == "pod-logs")
                    {
                        detailVm.SelectTabCommand.Execute("logs");
                        Settle(rounds: 30);
                    }
                    else if (scene == "pod-yaml")
                    {
                        detailVm.SelectTabCommand.Execute("yaml");
                        SettleUntil(() => detailVm.YamlText.Length > 0, maxRounds: 60);
                        // Show the editor mid-edit, so Revert/Apply are live.
                        detailVm.YamlText = detailVm.YamlText.Replace("qosClass: Burstable", "qosClass: Guaranteed", StringComparison.Ordinal);
                        Settle(rounds: 20);
                    }
                }
                break;

            case "real-apply":
            case "real-cluster":
                // The live Kubernetes adapter (KON-68) against whatever the kubeconfig points at.
                {
                    var live = vm.Clusters.FirstOrDefault(c => c.Backend.StartsWith("kubernetes:", StringComparison.Ordinal));
                    if (live is null)
                    {
                        Console.Error.WriteLine("No Kubernetes context found — rendering the fake cluster instead.");
                        vm.SwitchEngineCommand.Execute("fakecluster:prod-eu-west");
                    }
                    else
                    {
                        vm.SwitchEngineCommand.Execute(live.Backend);
                    }

                    SettleUntil(() => vm.IsClusterMode, maxRounds: 200);

                    if (scene == "real-apply")
                    {
                        // Dry-run a bundle against the live cluster: the plan comes from the API
                        // server's admission chain, not from a local guess.
                        vm.NavigateCommand.Execute("apply");
                        Settle(rounds: 40);
                        if (vm.CurrentPage is Kontena.App.ViewModels.ApplyManifestViewModel liveApply)
                        {
                            liveApply.YamlText = LiveApplySample;
                            liveApply.DryRunCommand.Execute(null);
                            SettleUntil(() => liveApply.HasPlan, maxRounds: 200);
                        }
                    }
                    else
                    {
                        vm.NavigateCommand.Execute("nodes");
                        Settle(rounds: 60);
                    }
                }

                break;

            case "apply":
            case "apply-plan":
            case "apply-done":
            case "apply-kustomize":
            case "apply-helm":
                vm.SwitchEngineCommand.Execute("fakecluster:prod-eu-west");
                SettleUntil(() => vm.IsClusterMode, maxRounds: 120);
                vm.NavigateCommand.Execute("apply");
                Settle(rounds: 30);

                // The render sources (KON-88, KON-89) build the repo's own samples, so the capture
                // shows a real kustomize/helm run rather than a mocked-up form.
                if (scene is "apply-kustomize" or "apply-helm"
                    && vm.CurrentPage is Kontena.App.ViewModels.ApplyManifestViewModel render)
                {
                    if (scene == "apply-kustomize")
                    {
                        render.SelectSourceCommand.Execute("Kustomize");
                        render.KustomizePath = RepoPath("samples/kustomize/overlays/prod");
                    }
                    else
                    {
                        render.SelectSourceCommand.Execute("Helm");
                        render.Chart = RepoPath("samples/helm/guestbook");
                        render.ReleaseName = "shop";
                        render.AddValuesFile(RepoPath("samples/helm/guestbook/values-prod.yaml"));
                        render.SetValues = "replicaCount=4";
                    }

                    render.RenderCommand.Execute(null);
                    SettleUntil(() => !render.IsRendering && render.HasDiagnostics, maxRounds: 120);

                    render.DryRunCommand.Execute(null);
                    SettleUntil(() => render.HasPlan, maxRounds: 60);
                    break;
                }

                if (scene != "apply" && vm.CurrentPage is Kontena.App.ViewModels.ApplyManifestViewModel apply)
                {
                    // A bundle that exercises every plan outcome: one change, one create, one no-op.
                    apply.YamlText = PlanSampleManifest;
                    apply.DryRunCommand.Execute(null);
                    SettleUntil(() => apply.HasPlan, maxRounds: 60);

                    if (scene == "apply-done")
                    {
                        apply.ApplyCommand.Execute(null);
                        SettleUntil(() => !apply.IsPreview, maxRounds: 60);
                    }
                }
                break;

            case "scale":
                vm.SwitchEngineCommand.Execute("fakecluster:prod-eu-west");
                SettleUntil(() => vm.IsClusterMode, maxRounds: 120);
                vm.NavigateCommand.Execute("workloads");
                Settle(rounds: 30);
                if (vm.CurrentPage is Kontena.App.ViewModels.ClusterWorkloadsViewModel wl)
                {
                    wl.Workloads.FirstOrDefault(w => w.CanScale)?.ScaleCommand.Execute(null);
                    Settle(rounds: 20);
                }
                break;

            case "restart":
                vm.SwitchEngineCommand.Execute("fakecluster:prod-eu-west");
                SettleUntil(() => vm.IsClusterMode, maxRounds: 120);
                vm.NavigateCommand.Execute("workloads");
                Settle(rounds: 30);
                if (vm.CurrentPage is Kontena.App.ViewModels.ClusterWorkloadsViewModel wlr)
                {
                    wlr.Workloads.FirstOrDefault(w => w.CanRestart)?.RestartCommand.Execute(null);
                    Settle(rounds: 20);
                }
                break;

            case "portforward":
            case "portforward-active":
                vm.SwitchEngineCommand.Execute("fakecluster:prod-eu-west");
                SettleUntil(() => vm.IsClusterMode, maxRounds: 120);
                vm.NavigateCommand.Execute("services");
                Settle(rounds: 30);
                if (vm.CurrentPage is Kontena.App.ViewModels.ClusterServicesViewModel svc)
                {
                    svc.Services.FirstOrDefault(s => s.CanForward)?.ForwardCommand.Execute(null);
                    Settle(rounds: 20);
                }
                if (scene == "portforward-active" && vm.Dialog is Kontena.App.ViewModels.PortForwardViewModel pf)
                {
                    pf.StartCommand.Execute(null);
                    Settle(rounds: 20);
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

    /// <summary>
    /// An absolute path to something in the repository. The capture runs from an output directory
    /// several levels down, so walk up until the repository root is recognisable.
    /// </summary>
    private static string RepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "samples")))
            dir = dir.Parent;

        return dir is null ? relative : Path.Combine(dir.FullName, relative);
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
