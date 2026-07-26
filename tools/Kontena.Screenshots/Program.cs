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
//         without the tool installed the capture shows the render's error state),
//         update-{toast,card,downloading,ready,failed} (the in-app updater, driven through the real
//         state machine against a fake update source), settings-updates and
//         settings-updates-unmanaged (the Updates category, managed and not),
//         cluster / cluster-{nodes,namespaces,workloads,pods,services} (the cluster browsers),
//         cluster-portforwards (all four port-forward states: active, dropped, remembered, paused —
//         reached by really switching backend and back, so it exercises the save/restore path),
//         pod / pod-logs / pod-yaml (pod detail),
//         backend-down (the state when the remembered backend is gone — the one scene
//         that deliberately does not take the demo-engine shortcut).
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
        // The app's ConnectPreferred honours this to boot straight into the demo engine — except
        // for the one scene whose whole subject is what happens when that does not work out.
        if (opts.Scene != "backend-down")
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

                // Pinned, not "last used": a capture must not depend on what a previous capture
                // happened to leave behind. The backend-down scene is the exception — it asks to
                // return to a backend that is deliberately not in the list.
                Startup = opts.Scene == "backend-down" ? StartupBackend.LastUsed : StartupBackend.Pinned,
                PinnedBackend = "docker",
                LastBackend = "kubernetes:corp-cluster",
                AutoDetectEngines = true,

                // The offer states must survive the check on launch: with the background download
                // on, "available" is gone before the scene can ask for anything.
                AutoDownloadUpdates = opts.Scene is not ("update-toast" or "update-card"),
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

            // Persist the scene's settings before anything reads them: parts of the app deliberately
            // re-read the store rather than trust a copy from launch (the updater does), and an empty
            // store would hand them defaults instead of this scene's choices.
            var store = new SettingsStore();
            store.Save(settings);

            // The update scenes need an updater with something to offer: a development run is not a
            // packaged install, so the real one can only ever render the "cannot update here" state.
            // settings-updates-unmanaged deliberately keeps the real service, which in a
            // development run is exactly the "cannot update here" case it wants to show.
            var updates = opts.Scene.StartsWith("update", StringComparison.Ordinal)
                          || opts.Scene == "settings-updates"
                ? new FakeUpdateService(
                    fail: opts.Scene == "update-failed",
                    holdAt: opts.Scene == "update-downloading" ? 62 : null)
                : null;

            var viewModel = new MainWindowViewModel(registry, store, settings, updates);

            var window = new MainWindow
            {
                DataContext = viewModel,
                Width = opts.Width,
                Height = opts.Height,
            };
            window.Show();

            // Let the fire-and-forget InitAsync → ConnectPreferred → ActivateAsync settle so the
            // container list (and its live log/stat streams) is populated before we drive the scene.
            SettleUntil(() => viewModel.IsReady || viewModel.IsBackendDown, maxRounds: 120);

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
            case "settings-about":
            case "settings-registries":
            case "settings-engines":
                vm.ShowSettingsCommand.Execute(null);
                if (scene is "settings-engines" or "settings-about" or "settings-registries"
                    && vm.SettingsPage is Kontena.App.ViewModels.SettingsViewModel s)
                {
                    s.SelectCategoryCommand.Execute(scene switch
                    {
                        "settings-about" => "about",
                        "settings-registries" => "registries",
                        _ => "engines",
                    });
                    Settle(rounds: 20);
                }

                break;

            // The update card (KON-110). Every scene drives the real state machine — check, then
            // download — rather than posing the view, so a shot that looks right also works.
            case "update-toast":
            case "update-card":
            case "update-downloading":
            case "update-ready":
            case "update-failed":
            case "settings-updates":
            case "settings-updates-unmanaged":
            {
                var settingsPage = vm.SettingsPage as Kontena.App.ViewModels.SettingsViewModel;
                if (scene.StartsWith("settings-updates", StringComparison.Ordinal))
                {
                    vm.ShowSettingsCommand.Execute(null);
                    settingsPage?.SelectCategoryCommand.Execute("updates");
                    Settle(rounds: 20);
                    break;
                }

                // The check on launch already ran — these scenes wait for where it lands rather
                // than starting a second one, which is both the real path and one racing download
                // fewer.
                SettleUntil(() => vm.Update.Stage != Kontena.App.ViewModels.UpdateStage.None, maxRounds: 200);

                if (scene == "update-toast")
                    break;                                   // the toast is up; leave the card closed

                if (scene == "update-downloading")
                    SettleUntil(() => vm.Update.Percent >= 62, maxRounds: 200);
                else if (scene == "update-ready")
                    SettleUntil(() => vm.Update.Stage == Kontena.App.ViewModels.UpdateStage.Ready, maxRounds: 400);
                else if (scene == "update-failed")
                    SettleUntil(() => vm.Update.Stage == Kontena.App.ViewModels.UpdateStage.Failed, maxRounds: 400);

                vm.Update.OpenCardCommand.Execute(null);

                Settle(rounds: 20);
                break;
            }

            case "volume-browse":
            case "volume-browse-nested":
                vm.NavigateCommand.Execute("volumes");
                SettleUntil(() => vm.Volumes is { HasLoaded: true }, maxRounds: 80);
                vm.Volumes!.Items.First(v => v.Name == "pgdata").BrowseCommand.Execute(null);
                SettleUntil(() => vm.Dialog is Kontena.App.ViewModels.BrowseVolumeViewModel { IsLoading: false },
                    maxRounds: 120);

                if (scene == "volume-browse-nested")
                {
                    // Open a directory the way a user does, so the shot proves navigation and not just
                    // the first listing.
                    var browser = (Kontena.App.ViewModels.BrowseVolumeViewModel)vm.Dialog!;
                    browser.OpenCommand.Execute(browser.Entries.First(e => e.Name == "logs"));
                    SettleUntil(() => !browser.IsLoading && browser.Path == "/logs", maxRounds: 120);
                }

                Settle(rounds: 10);
                break;

            case "new-volume":
                vm.NavigateCommand.Execute("volumes");
                Settle(rounds: 20);
                vm.Volumes!.CreateVolumeCommand.Execute(null);
                Settle(rounds: 10);
                break;

            case "network-attachments":
                vm.NavigateCommand.Execute("networks");
                SettleUntil(() => vm.Networks is { HasLoaded: true }, maxRounds: 80);
                vm.Networks!.Items.First(n => n.CanAttach && !n.IsBuiltIn).AttachmentsCommand.Execute(null);
                SettleUntil(
                    () => vm.Dialog is Kontena.App.ViewModels.NetworkAttachmentsViewModel { IsBusy: false },
                    maxRounds: 120);
                Settle(rounds: 10);
                break;

            case "new-network":
                vm.NavigateCommand.Execute("networks");
                Settle(rounds: 20);
                vm.Networks!.CreateNetworkCommand.Execute(null);
                Settle(rounds: 10);
                break;

            case "cluster-portforwards":
                // All three states on one page, and every one of them reached the way the app does it:
                // two forwards are opened, the backend is switched away and back (which persists them
                // and restores them closed, KON-105), one is reopened, and a third is dropped (KON-102).
                {
                    var fake = new Kontena.Core.Orchestration.Fakes.FakeClusterEngine();
                    var pod = new Kontena.Core.Orchestration.Models.ResourceRef(
                        Kontena.Core.Orchestration.Models.GroupVersionKind.Pod, "app", "api-7d9c");
                    var service = new Kontena.Core.Orchestration.Models.ResourceRef(
                        Kontena.Core.Orchestration.Models.GroupVersionKind.Service, "app", "api");
                    var postgres = new Kontena.Core.Orchestration.Models.ResourceRef(
                        Kontena.Core.Orchestration.Models.GroupVersionKind.Service, "app", "postgres");

                    vm.SwitchEngineCommand.Execute("fakecluster:prod-eu-west");
                    SettleUntil(() => vm.IsClusterMode, maxRounds: 120);
                    vm.PortForwards.StartAsync(fake, service, "api · app", 80, 8080).GetAwaiter().GetResult();
                    vm.PortForwards.StartAsync(fake, pod, "api-7d9c · app", 8080, 9229).GetAwaiter().GetResult();

                    // Away and back: the real save-and-restore path, not a hand-built list.
                    vm.SwitchEngineCommand.Execute("docker");
                    SettleUntil(() => !vm.IsClusterMode && vm.IsReady, maxRounds: 120);
                    vm.SwitchEngineCommand.Execute("fakecluster:prod-eu-west");
                    SettleUntil(() => vm.IsClusterMode, maxRounds: 120);

                    vm.PortForwards.ReconnectAsync(vm.PortForwards.Forwards[0]).GetAwaiter().GetResult();

                    vm.PortForwards.StartAsync(fake, postgres, "postgres · app", 5432, 5432).GetAwaiter().GetResult();
                    fake.LastPortForward!.Drop("The pod was replaced; the cluster refused a new connection.");

                    // And one paused by hand, so all four states are on the page at once.
                    var paused = vm.PortForwards
                        .StartAsync(fake, service, "api · app", 8080, 3000).GetAwaiter().GetResult();
                    vm.PortForwards.PauseAsync(paused).GetAwaiter().GetResult();

                    vm.NavigateCommand.Execute("portforwards");
                    Settle(rounds: 30);
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
            case "real-helm":
            case "real-shell":
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

                    if (scene == "real-shell")
                    {
                        // Pod detail against the live cluster, on the Shell tab — the terminal only
                        // appears when Capabilities.Exec is true (KON-97).
                        vm.NavigateCommand.Execute("pods");
                        Settle(rounds: 60);
                        if (vm.CurrentPage is Kontena.App.ViewModels.ClusterPodsViewModel livePods)
                        {
                            livePods.Pods.FirstOrDefault()?.OpenCommand.Execute(null);
                            Settle(rounds: 60);
                        }

                        if (vm.CurrentPage is Kontena.App.ViewModels.ClusterPodDetailViewModel liveDetail)
                        {
                            liveDetail.SelectTabCommand.Execute("shell");
                            Settle(rounds: 80);
                        }
                    }
                    else if (scene == "real-apply")
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
                    else if (scene == "real-helm")
                    {
                        // A chart rendered locally, then held against the live cluster. Defaults to
                        // the repository's own sample; KONTENA_SHOT_CHART points it at a real chart
                        // (e.g. "cilium/cilium"), which is how the plan's filter gets exercised —
                        // a big chart is mostly resources that are not changing.
                        vm.NavigateCommand.Execute("apply");
                        Settle(rounds: 40);
                        if (vm.CurrentPage is Kontena.App.ViewModels.ApplyManifestViewModel liveHelm)
                        {
                            var chart = Environment.GetEnvironmentVariable("KONTENA_SHOT_CHART");
                            liveHelm.SelectSourceCommand.Execute("Helm");
                            liveHelm.Chart = string.IsNullOrWhiteSpace(chart) ? RepoPath("samples/helm/guestbook") : chart;
                            liveHelm.ReleaseName = Environment.GetEnvironmentVariable("KONTENA_SHOT_RELEASE") ?? "demo";

                            liveHelm.RenderCommand.Execute(null);
                            SettleUntil(() => !liveHelm.IsRendering && liveHelm.HasDiagnostics, maxRounds: 400);

                            liveHelm.DryRunCommand.Execute(null);
                            SettleUntil(() => liveHelm.HasPlan, maxRounds: 400);
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

                        // Shows the repository panel on a machine that has repos configured; on one
                        // that has none it stays hidden, which is also what the user would see.
                        render.LoadReposCommand.Execute(null);
                        SettleUntil(() => render.Repos.Count > 0, maxRounds: 40);
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

            case "backend-down":
                // Nothing to drive: startup already landed in the down state, because the settings
                // this scene boots with ask to return to a backend that is not there (KON-98).
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
