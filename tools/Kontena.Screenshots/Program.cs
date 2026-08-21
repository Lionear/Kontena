using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using System.Collections.Generic;
using Kontena.Adapters.Docker;
using Kontena.Adapters.Kubernetes;
using Kontena.Adapters.Podman;
using Kontena.App;
using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.App.Views;
using Kontena.Sdk.Models;
using Kontena.Sdk;
using Kontena.Sdk.Tooling;
using Kontena.Sdk.Tooling.Fakes;
using Kontena.Engines.Fakes;
using HostApp = Kontena.App.App;
using Kontena.Core.Models;
using Kontena.Core.Orchestration;
using Kontena.Engines;

namespace Kontena.Screenshots;

// Renders a scene of the real Kontena app to a PNG using Avalonia's headless + Skia backend — no
// display, no real container engine. The built-in in-memory FakeEngine supplies the demo data
// (the same seed the app ships for dev/demo), so every pixel comes from the real views and styles.
//
// Usage:
//   dotnet run --project tools/Kontena.Screenshots -- --scene containers --theme dark --out shots/containers.png [--size 1180x760] [--scale 2]
//
// --size is the window in layout pixels; --scale (1-4, default 1) is the device pixel ratio on top of
// it, so --scale 2 writes a 2x PNG of the same composition rather than a bigger window.
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
//         alerts (KON-393 — the Alerts page, with the notice that says how it keeps up),
//         cluster-portforwards (all four port-forward states: active, dropped, remembered, paused —
//         reached by really switching backend and back, so it exercises the save/restore path),
//         cluster-node-drawer / cluster-namespace-drawer (the detail drawer over its list, KON-307),
//         pod / pod-logs / pod-yaml (pod detail),
//         pod-config (KON-390 — the Overview tab as a full page, with a Secret row of
//         Config & secrets open and one of its values revealed),
//         pod-env (KON-416 — the same page, with the Environment variables section's own eye
//         pressed, so the shot carries both a reference and the value behind it),
//         tag-push-image (KON-387 — the Tag-and-push modal over the Images page),
//         backend-down (the state when the remembered backend is gone — the one scene
//         that deliberately does not take the demo-engine shortcut),
//         onboarding (the first-run wizard) and onboarding-again (the same wizard reached back from
//         the engine-down card by running its own "Set up again" command, so the shot cannot show a
//         route that the button does not really take),
//         onboarding-start-assist (the wizard's offer to start a stopped Podman — a scripted
//         systemctl and an unreachable Podman, driven through the real PodmanSocketFix; Linux-only,
//         because the fix itself is, and on other systems the block correctly never appears),
//         settings-clusters (KON-109/KON-76 — the local-cluster page; reads this machine, so the
//         shot differs per box by design), settings-clusters-new (the create form, reached by running
//         the page's own command),
//         settings-tools (KON-266 — the external tools, grouped by what you need them for; reads this
//         machine for the same reason settings-clusters does),
//         confirm-delete-volume and confirm-remove-kubeconfig (KON-126 — the destructive
//         confirmation and the deliberately non-destructive one, both reached by running the
//         row's own command so the shot cannot show a dialog the button does not raise).
internal static class Program
{
    // The demo backends wear the marks of what they stand in for (KON-80): a shot of "Docker" with a
    // letter badge would show a chip the app no longer draws for a real engine.
    private static readonly BackendChipStyle Kubernetes =
        new(KubernetesBrand.Glyph, KubernetesBrand.Accent);

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
        // onboarding-again starts life on the down card, so it needs the same "no shortcut" treatment.
        if (opts.Scene is not ("backend-down" or "onboarding-again"))
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

                // The scenes that are about not having been here before.
                Onboarded = opts.Scene is not ("onboarding" or "onboarding-start-assist"),

                // Pinned, not "last used": a capture must not depend on what a previous capture
                // happened to leave behind. The down-card scenes are the exception — they ask to
                // return to a backend that is deliberately not in the list.
                Startup = opts.Scene is "backend-down" or "onboarding-again"
                    ? StartupBackend.LastUsed
                    : StartupBackend.Pinned,
                PinnedBackend = "docker",
                LastBackend = "kubernetes:corp-cluster",
                AutoDetectEngines = true,

                // The offer states must survive the check on launch: with the background download
                // on, "available" is gone before the scene can ask for anything.
                AutoDownloadUpdates = opts.Scene is not ("update-toast" or "update-card"),

                // An added kubeconfig, so the row that can be removed is in frame (KON-122). The file
                // need not exist: the point of the shot is the management row, and a config on a
                // disconnected drive is a state the list has to survive anyway.
                KubeconfigPaths = opts.Scene is "settings-engines-kubeconfigs" or "confirm-remove-kubeconfig"
                    ? ["/srv/kubeconfigs/acme.yaml"]
                    : [],

                // A stored remote, so the list has a row to edit (KON-125). For the KON-181 shot a
                // second one that never came from a form — a settings file edited by hand, or synced
                // from another machine — because that is the row that has to explain itself.
                RemoteEngines = opts.Scene is "settings-engines-edit" or "settings-engines-badhost"
                    ? [
                        new RemoteEngine("r1", "Build server", RemoteEngineTransport.Ssh,
                            "build-01.example.com", User: "deploy"),
                        .. opts.Scene == "settings-engines-badhost"
                            ? new[]
                            {
                                new RemoteEngine("r2", "Imported host", RemoteEngineTransport.Ssh,
                                    "-oProxyCommand=touch /tmp/pwned"),
                            }
                            : [],
                    ]
                    : [],
            };
            // Present the built-in demo seed under Docker's name/chip — the shots read as a real
            // Docker session (the app itself always keeps the honest "Fake engine" identity).
            var providers = new List<IBackendProvider>
            {
                new FakeEngineProvider("docker", "Docker", "D",
                    new BackendChipStyle(DockerBrand.Glyph, DockerBrand.Accent)),
                new FakeClusterProvider("prod-eu-west", "GKE", Kubernetes),
                new FakeClusterProvider("staging", "EKS", Kubernetes),
                new FakeClusterProvider("minikube", "MK", Kubernetes),
            };

            // Every other scene stays on the seeded fakes so shots are reproducible; only the
            // real-* scenes reach for the machine's actual kubeconfig (KON-68/86).
            if (opts.Scene.StartsWith("real-", StringComparison.Ordinal))
                providers.AddRange(KubernetesClusterProvider.DiscoverAll());

            // A machine whose only engine is a Podman that will not answer. Nothing reachable, because
            // the subject of the scene is the row that cannot be picked and what the wizard offers to
            // do about it.
            if (opts.Scene == "onboarding-start-assist")
            {
                providers.Clear();
                providers.Add(new StoppedPodmanProvider());
            }

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
                          || opts.Scene is "settings-updates" or "settings-updates-following"
                ? new FakeUpdateService(
                    fail: opts.Scene == "update-failed",
                    holdAt: opts.Scene == "update-downloading" ? 62 : null,

                    // A nightly build with no stored choice, which is the whole subject of that scene.
                    buildChannel: opts.Scene == "settings-updates-following"
                        ? UpdateChannel.Nightly
                        : UpdateChannel.Stable)
                : null;

            // Scripted systemd state for the start-assist scene. The check itself is the real
            // PodmanSocketFix — only systemctl's answer is supplied, the way the update scenes supply
            // an update source. Asking the renderer's own machine would make the shot depend on
            // whether the person capturing it happens to run Podman.
            var tools = opts.Scene == "onboarding-start-assist"
                ? new FakeToolRunner()
                    .Install(new ExternalTool("systemctl", "systemctl", ["--version"], []))
                    .When(i => i.Arguments.Contains("is-active"), output: ["inactive"], exitCode: 3)
                : null;

            var viewModel = new MainWindowViewModel(registry, store, settings, updates, tools);

            var window = new MainWindow
            {
                DataContext = viewModel,
                Width = opts.Width,
                Height = opts.Height,
            };

            // --size stays in layout pixels so a scene keeps its composition; --scale only raises the
            // device pixel ratio, the way a HiDPI display would. A 2x capture is what the website wants:
            // its prose column is narrower than the window, so a 1x shot is downscaled and the app's
            // text turns to mush.
            window.SetRenderScaling(opts.Scale);
            window.Show();

            // Let the fire-and-forget InitAsync → ConnectPreferred → ActivateAsync settle so the
            // container list (and its live log/stat streams) is populated before we drive the scene.
            SettleUntil(() => viewModel.IsReady || viewModel.IsBackendDown || viewModel.IsOnboarding,
                maxRounds: 120);

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
            case "onboarding": // the launch state already is the wizard
                break; // default page

            // The offer arrives after the screen is already up — systemd is asked in the background,
            // on purpose, so a slow answer cannot hold up drawing the wizard.
            case "onboarding-start-assist":
                SettleUntil(() => vm.Onboarding?.FixCommandLine is not null, maxRounds: 120);
                break;

            // The way back: run the down card's own command rather than calling EnterOnboarding, so
            // a button wired to nothing would show up here as a shot still on the down card.
            case "onboarding-again":
                vm.RunSetupCommand.Execute(null);
                SettleUntil(() => vm.IsOnboarding, maxRounds: 120);
                break;

            // No scene for the switcher's "n new clusters found" row (KON-120): it lives in a flyout,
            // and a flyout renders into its own popup window rather than into this frame. Its count
            // comes from ClusterVisibility, which is covered by tests; the row itself is checked by
            // hand. Faking a shot of it would prove nothing.

            case "images":
            case "volumes":
            case "networks":
            case "projects":
                vm.NavigateCommand.Execute(scene);
                break;

            // The two ends of KON-126: a delete that destroys data, and one that only stops reading a
            // file. Both are raised by executing the row's own command, so a dialog that the button
            // does not actually open cannot be photographed here.
            case "confirm-delete-volume":
                vm.NavigateCommand.Execute("volumes");
                Settle(rounds: 20);
                if (vm.CurrentPage is Kontena.App.ViewModels.VolumesViewModel volumes
                    && volumes.Items.Count > 0)
                {
                    // A mounted one, so the shot includes the sentence naming what loses it.
                    var row = volumes.Items.FirstOrDefault(v => v.MountedBy.Count > 0) ?? volumes.Items[0];
                    row.DeleteCommand.Execute(null);
                    Settle(rounds: 20);
                }

                break;

            // The widest delete in the app, and the one the itemised inventory was built for (KON-162).
            case "confirm-project-down":
                Settle(rounds: 20);
                if (vm.Containers?.Items.OfType<Kontena.App.ViewModels.ComposeGroupRowViewModel>()
                        .FirstOrDefault() is { } project)
                {
                    project.DownCommand.Execute(null);
                    Settle(rounds: 20);
                }

                break;

            case "confirm-remove-kubeconfig":
                vm.ShowSettingsCommand.Execute(null);
                Settle(rounds: 20);
                if (vm.SettingsPage is Kontena.App.ViewModels.SettingsViewModel kubePage)
                {
                    kubePage.SelectCategoryCommand.Execute("engines");
                    Settle(rounds: 20);

                    var source = kubePage.Kubeconfigs.FirstOrDefault(k => k.CanRemove);
                    if (source is not null)
                        kubePage.RemoveKubeconfigCommand.Execute(source);

                    Settle(rounds: 20);
                }

                break;

            case "settings":
            case "settings-keyboard":
            case "settings-about":
            case "settings-registries":
            case "settings-engines":
            case "settings-engines-tcp":
            case "settings-engines-badhost":
            case "settings-engines-edit":
            case "settings-engines-named":
            case "settings-engines-clusters":
            case "settings-clusters":
            case "settings-clusters-new":
            case "settings-tools":
            case "settings-engines-kubeconfigs":
                vm.ShowSettingsCommand.Execute(null);
                if (vm.SettingsPage is Kontena.App.ViewModels.SettingsViewModel s)
                {
                    s.SelectCategoryCommand.Execute(scene switch
                    {
                        "settings-about" => "about",
                        "settings-registries" => "registries",
                        "settings" or "settings-keyboard" => "general",
                        "settings-clusters" or "settings-clusters-new" => "clusters",
                        "settings-tools" => "tools",
                        _ => "engines",
                    });

                    // Reads this machine, exactly like the local-cluster page: a posed "kubectl
                    // detected" would render the same whether or not the detection works (KON-266).
                    if (scene == "settings-tools" && s.Tools is { } tools)
                    {
                        SettleUntil(() => tools.HasLoaded, maxRounds: 200);
                        Settle(rounds: 20);
                    }

                    // The states of the keyboard section that only exist after someone has used it
                    // (KON-180): a changed row with its reset button, Restore defaults, a row still
                    // listening, and a refusal. Driven through the page's own methods, so the shot
                    // cannot show a state the UI does not produce.
                    if (scene == "settings-keyboard")
                    {
                        var rows = s.Shortcuts;
                        rows.First(r => r.Action.Id == ShellActions.RefreshPage).Offer("Ctrl+Shift+R");
                        rows.First(r => r.Action.Id == ShellActions.FocusSearch).RecordCommand.Execute(null);
                        rows.First(r => r.Action.Id == ShellActions.GoBack).Offer("Ctrl+C");
                        Settle(rounds: 10);
                    }

                    // Local clusters reads the machine it runs on (KON-109), so this shot shows what
                    // is actually installed here rather than a posed list — which is the point: a
                    // scene that faked "kind detected" would render identically whether or not the
                    // detection works.
                    if (scene is "settings-clusters" or "settings-clusters-new" && s.LocalClusters is { } clusters)
                    {
                        SettleUntil(() => clusters.HasLoaded, maxRounds: 200);
                        Settle(rounds: 20);

                        // The form is reached by running the page's own command, so the shot cannot show
                        // a screen the button does not open (the lesson from KON-117).
                        if (scene == "settings-clusters-new")
                        {
                            clusters.NewClusterCommand.Execute(null);
                            if (clusters.Form is { } form)
                            {
                                form.Name = "dev";
                                form.WorkerNodes = "2";
                                form.IngressReady = true;
                                form.Ports[0].HostPort = "8080";
                                form.Ports[0].NodePort = "80";
                            }

                            Settle(rounds: 20);
                        }
                    }

                    // The TCP form is where the security decision lives, so it gets its own shot.
                    if (scene == "settings-engines-tcp")
                        s.SetRemoteTransportCommand.Execute("tcp");

                    // A host ssh would read as one of its own options (KON-181). Typed into the form,
                    // because the claim worth checking is that the disabled submit button explains
                    // itself rather than just going grey.
                    if (scene == "settings-engines-badhost")
                    {
                        s.RemoteName = "Build server";
                        s.RemoteHost = "-oProxyCommand=touch /tmp/pwned";
                    }

                    // Editing is driven through the row's own command (KON-125): the shot has to show
                    // the stored values really loaded, not a form someone filled in to look like it.
                    if (scene == "settings-engines-edit" && s.RemoteEngines.Count > 0)
                        s.EditRemoteCommand.Execute(s.RemoteEngines[0]);

                    // Renaming (KON-119) is typed into the row, not poked into settings — the claim worth
                    // checking is that the switcher pill and the list follow, and only the real path does
                    // that. The shot is taken with the sidebar in frame for exactly that reason.
                    // Hiding a cluster goes through the real row, so the shot shows what actually
                    // happens to the switcher when one is unticked (KON-120).
                    if (scene == "settings-engines-clusters" && s.Clusters.Count > 1)
                        s.Clusters[^1].IsShown = false;

                    if (scene == "settings-engines-named" && s.BackendNames.Count > 0)
                    {
                        s.BackendNames[0].Name = "Work laptop";
                        if (s.BackendNames.Count > 1)
                            s.BackendNames[1].Name = "Production EU";
                    }

                    Settle(rounds: 20);
                }


                break;

            // The add wizard (KON-118), driven through the switcher's own command rather than by
            // constructing the dialog — the row spent a release doing nothing, and only running what the
            // button actually runs would have shown that.
            //
            // The Testing and Connected states are deliberately absent: both need a remote host that
            // answers, and a scene that posed them would render perfectly while the wizard was broken.
            // Refused is here because it can be produced honestly — a host that does not resolve.
            case "add-engine":
            case "add-engine-remote":
            case "add-engine-tcp":
            case "add-engine-kube":
            case "add-engine-refused":
                vm.ShowAddBackendCommand.Execute(null);
                Settle(rounds: 20);

                if (vm.Dialog is Kontena.App.ViewModels.AddBackendViewModel wizard)
                {
                    if (scene == "add-engine-kube")
                    {
                        wizard.ChooseKubernetesCommand.Execute(null);
                    }
                    else if (scene != "add-engine")
                    {
                        wizard.ChooseRemoteEngineCommand.Execute(null);
                        wizard.Host = "build-01.example.com";
                        wizard.Name = "Build server";

                        if (scene == "add-engine-tcp")
                        {
                            wizard.SetTransportCommand.Execute("tcp");
                            wizard.CertificateDirectory = "~/.docker/certs/build-01";
                        }
                        else if (scene == "add-engine-refused")
                        {
                            // .invalid never resolves, by RFC. The wizard runs its real test and lands on
                            // its real failure page.
                            wizard.Host = "build-01.invalid";
                            wizard.User = "deploy";
                            wizard.PrimaryCommand.Execute(null);
                            Settle(rounds: 120);
                        }
                        else
                        {
                            wizard.User = "deploy";
                        }
                    }

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
            case "settings-updates-following":
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

            // Raised from the row's own command, so a button wired to nothing shows up here as a shot of
            // the plain images page (KON-387).
            case "tag-push-image":
                vm.NavigateCommand.Execute("images");
                SettleUntil(() => vm.Images is { HasLoaded: true }, maxRounds: 80);
                vm.Images!.Items[0].TagAndPushCommand.Execute(null);
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
                    var pod = new Kontena.Sdk.Orchestration.Models.ResourceRef(
                        Kontena.Sdk.Orchestration.Models.GroupVersionKind.Pod, "app", "api-7d9c");
                    var service = new Kontena.Sdk.Orchestration.Models.ResourceRef(
                        Kontena.Sdk.Orchestration.Models.GroupVersionKind.Service, "app", "api");
                    var postgres = new Kontena.Sdk.Orchestration.Models.ResourceRef(
                        Kontena.Sdk.Orchestration.Models.GroupVersionKind.Service, "app", "postgres");

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

            case "alerts":
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
                else if (scene == "alerts")
                {
                    vm.NavigateCommand.Execute("alerts");
                    Settle(rounds: 40);
                }

                break;

            case "cluster-node-drawer":
            case "cluster-namespace-drawer":
                // The detail drawer over the list it was opened from (KON-307). Reached through the
                // row's own Open command, so the shot cannot show a drawer the card does not raise.
                vm.SwitchEngineCommand.Execute("fakecluster:prod-eu-west");
                SettleUntil(() => vm.IsClusterMode, maxRounds: 120);
                vm.NavigateCommand.Execute(scene == "cluster-node-drawer" ? "nodes" : "namespaces");
                Settle(rounds: 30);
                if (vm.CurrentPage is Kontena.App.ViewModels.ClusterNodesViewModel drawerNodes)
                    drawerNodes.Items.FirstOrDefault()?.OpenCommand.Execute(null);
                else if (vm.CurrentPage is Kontena.App.ViewModels.ClusterNamespacesViewModel drawerNs)
                    drawerNs.Items.FirstOrDefault()?.OpenCommand.Execute(null);
                Settle(rounds: 30);
                break;

            // KON-330: a Secret or ConfigMap opened as a detail, where its keys now live. The list
            // behind it is the shot's other half — the expander that used to unfold the keys in place
            // is gone, so the row is a link like every other cluster list.
            case "cluster-secrets":
            case "secret-detail":
            case "secret-detail-used-by":
            case "configmap-detail":
                vm.SwitchEngineCommand.Execute("fakecluster:prod-eu-west");
                SettleUntil(() => vm.IsClusterMode, maxRounds: 120);
                vm.NavigateCommand.Execute(scene == "configmap-detail" ? "configmaps" : "secrets");
                Settle(rounds: 30);

                if (scene != "cluster-secrets")
                {
                    // The one the ticket is about: an Opaque secret with two text values, and four
                    // pods reading it two different ways.
                    var wanted = scene == "configmap-detail" ? "web-config" : "postgres-credentials";
                    if (vm.CurrentPage is Kontena.App.ViewModels.ClusterSecretsViewModel secrets)
                        secrets.Items.FirstOrDefault(r => r.Name == wanted)?.OpenCommand.Execute(null);
                    else if (vm.CurrentPage is Kontena.App.ViewModels.ClusterConfigMapsViewModel maps)
                        maps.Items.FirstOrDefault(r => r.Name == wanted)?.OpenCommand.Execute(null);

                    Settle(rounds: 30);
                }

                if (scene == "secret-detail-used-by" && vm.Detail is Kontena.App.ViewModels.ClusterConfigDetailViewModel used)
                {
                    used.SelectTabCommand.Execute("pods");
                    Settle(rounds: 30);
                }

                break;

            case "pod":
            case "pod-logs":
            case "pod-logs-tail":
            case "pod-yaml":
            case "pod-config":
            case "pod-env":
                vm.SwitchEngineCommand.Execute("fakecluster:prod-eu-west");
                SettleUntil(() => vm.IsClusterMode, maxRounds: 120);
                vm.NavigateCommand.Execute("pods");
                Settle(rounds: 30);
                if (vm.CurrentPage is Kontena.App.ViewModels.ClusterPodsViewModel pods)
                {
                    pods.Items.FirstOrDefault()?.OpenCommand.Execute(null);
                    Settle(rounds: 30);
                }

                // Config & secrets sits below the container table, which the drawer cannot show at
                // once — so this one scene takes the detail's own "open as a page" command (KON-307)
                // rather than a wider drawer the app has no button for.
                if (scene is "pod-config" or "pod-env")
                {
                    vm.OpenDetailAsPageCommand.Execute(null);
                    Settle(rounds: 20);
                }

                // The detail lives in the drawer (KON-307) unless it was just opened as a page.
                if ((vm.Detail ?? vm.CurrentPage) is Kontena.App.ViewModels.ClusterPodDetailViewModel detailVm)
                {
                    if (scene is "pod-logs" or "pod-logs-tail")
                    {
                        detailVm.SelectTabCommand.Execute("logs");
                        Settle(rounds: 30);

                        // More lines than fit, so the shot says where the view actually sits (KON-165).
                        // The fake pod produces four; four cannot show a scroll position at all, which
                        // is how five views shipped without one.
                        if (scene == "pod-logs-tail")
                        {
                            for (var i = 1; i <= 200; i++)
                            {
                                detailVm.Lines.Add(new Kontena.App.ViewModels.LogLineViewModel(
                                    new Kontena.Sdk.Models.LogEntry(
                                        DateTimeOffset.UnixEpoch.AddSeconds(i),
                                        Kontena.Sdk.Models.LogSource.Stdout, $"line {i}")));
                            }

                            Settle(rounds: 40);
                        }
                    }
                    else if (scene == "pod-yaml")
                    {
                        detailVm.SelectTabCommand.Execute("yaml");
                        SettleUntil(() => detailVm.YamlText.Length > 0, maxRounds: 60);
                        // Show the editor mid-edit, so Revert/Apply are live.
                        detailVm.YamlText = detailVm.YamlText.Replace("qosClass: Burstable", "qosClass: Guaranteed", StringComparison.Ordinal);
                        Settle(rounds: 20);
                    }
                    else if (scene == "pod-config")
                    {
                        // Opened through the row's own command, so the shot cannot show keys the
                        // collapsed row does not really fetch (KON-390). A Secret, because the
                        // masking and the eye are the half worth documenting; one key is then
                        // revealed so both states stand in the same frame. Named rather than
                        // "the first Secret", which is the pull secret here — and a pull secret
                        // holds no keys, so that row unfolds onto nothing.
                        var config = detailVm.ConfigRows.First(
                            r => r.Reference.Name == "postgres-credentials");
                        config.ToggleCommand.Execute(null);
                        SettleUntil(() => config is { IsExpanded: true, IsBusy: false }, maxRounds: 120);

                        var key = config.Keys.FirstOrDefault();
                        key?.ToggleCommand.Execute(null);
                        SettleUntil(() => key is null or { IsRevealed: true, IsBusy: false }, maxRounds: 120);
                        Settle(rounds: 20);
                    }
                    else if (scene == "pod-env")
                    {
                        // Pressed through the row's own reveal, so the shot cannot show a value the
                        // eye does not really fetch. One of the three is opened and the other two are
                        // left alone, because the section's subject is the pair: a reference that
                        // names where the value lives, and the value once you ask for it (KON-416).
                        var secret = detailVm.EnvGroups
                            .SelectMany(g => g.Rows)
                            .First(r => r.Secret is not null)
                            .Secret!;

                        secret.ToggleCommand.Execute(null);
                        SettleUntil(() => secret is { IsRevealed: true, IsBusy: false }, maxRounds: 120);
                        Settle(rounds: 20);
                    }
                }
                break;

            // Workloads split per kind (KON-169): the nav group expanded, and one kind's page. Both
            // reached through the nav, so a scene cannot show a page the sidebar has no way of opening.
            case "cluster-workloads-expanded":
            case "cluster-workloads-dashboard":
            case "cluster-cronjobs":
            case "cluster-deployments":
                vm.SwitchEngineCommand.Execute("fakecluster:prod-eu-west");
                SettleUntil(() => vm.IsClusterMode, maxRounds: 120);
                // Clicking Workloads opens the group; it does not load every kind at once (KON-169).
                vm.NavigateCommand.Execute("overview");
                SettleUntil(() => vm.NavGroups.SelectMany(g => g.Items).Any(i => i.Key == "workloads"), maxRounds: 60);
                vm.NavigateCommand.Execute("workloads");
                SettleUntil(() => vm.NavGroups.SelectMany(g => g.Items).Any(i => i.IsChild), maxRounds: 60);

                if (scene == "cluster-cronjobs")
                    vm.NavigateCommand.Execute("workloads:CronJob");
                else if (scene == "cluster-deployments")
                    vm.NavigateCommand.Execute("workloads:Deployment");

                Settle(rounds: 30);
                break;

            // Workload and Service detail (KON-166, KON-167). Reached the way a user reaches them —
            // by opening a row — so a scene cannot show a page the list has no way of opening.
            case "workload":
            case "workload-pods":
            case "workload-cronjob":
            case "service":
            case "service-pods":
                vm.SwitchEngineCommand.Execute("fakecluster:prod-eu-west");
                SettleUntil(() => vm.IsClusterMode, maxRounds: 120);
                vm.NavigateCommand.Execute(scene.StartsWith("service", StringComparison.Ordinal) ? "services" : "workloads");
                Settle(rounds: 30);

                if (vm.CurrentPage is Kontena.App.ViewModels.ClusterWorkloadsViewModel workloads)
                {
                    var row = scene == "workload-cronjob"
                        ? workloads.Items.FirstOrDefault(w => w.Kind == "CronJob")
                        : workloads.Items.FirstOrDefault();
                    row?.OpenCommand.Execute(null);
                    Settle(rounds: 30);
                }
                else if (vm.CurrentPage is Kontena.App.ViewModels.ClusterServicesViewModel services)
                {
                    services.Items.FirstOrDefault()?.OpenCommand.Execute(null);
                    Settle(rounds: 30);
                }

                if (scene.EndsWith("-pods", StringComparison.Ordinal)
                    && vm.CurrentPage is Kontena.App.ViewModels.ClusterObjectDetailViewModel objectDetail)
                {
                    objectDetail.SelectTabCommand.Execute("pods");
                    SettleUntil(() => !objectDetail.PodsLoading, maxRounds: 60);
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
                            livePods.Items.FirstOrDefault()?.OpenCommand.Execute(null);
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
                    wl.Items.FirstOrDefault(w => w.CanScale)?.ScaleCommand.Execute(null);
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
                    wlr.Items.FirstOrDefault(w => w.CanRestart)?.RestartCommand.Execute(null);
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
                    svc.Items.FirstOrDefault(s => s.CanForward)?.ForwardCommand.Execute(null);
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
        // Containers only — the list holds Compose headings too since KON-159, and a heading has no
        // detail page to open.
        var rows = vm.Containers?.Items.OfType<ContainerRowViewModel>().ToList() ?? [];
        var row = rows.FirstOrDefault(c => c.IsRunning) ?? rows.FirstOrDefault();
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

    private sealed record Options(string Scene, string Theme, string Out, int Width, int Height, double Scale)
    {
        public static Options Parse(string[] args)
        {
            string scene = "containers", theme = "dark", @out = "screenshot.png";
            int width = 1180, height = 760;
            var scale = 1.0;

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
                    case "--scale":
                        if (double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out var s)
                            && s is > 0 and <= 4)
                        {
                            scale = s;
                        }
                        break;
                }
            }

            return new Options(scene, theme, @out, width, height, scale);
        }
    }

    /// <summary>A Podman that is installed but will not answer — the probe behind a "Not running" row.</summary>
    private sealed class StoppedPodmanProvider : IBackendProvider
    {
        public string Backend => "podman";
        public string DisplayName => "Podman";
        public string Chip => "P";
        public BackendChipStyle? ChipStyle => new(PodmanBrand.Glyph, PodmanBrand.Accent);
        public BackendKind Kind => BackendKind.Engine;
        public IBackend CreateBackend() => new StoppedBackend();

        private sealed class StoppedBackend : IBackend
        {
            public string Backend => "podman";
            public ValueTask<BackendInfo> GetInfoAsync(CancellationToken ct = default)
                => throw new InvalidOperationException("the socket did not answer");
            public ValueTask PingAsync(CancellationToken ct = default)
                => throw new InvalidOperationException("the socket did not answer");
        }
    }
}
