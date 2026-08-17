using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Adapters.Podman;
using Kontena.Adapters.RemoteClusters;
using Kontena.App.Services;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Core.Orchestration.Preflight;
using Kontena.Sdk;
using Kontena.Sdk.Errors;
using Kontena.Sdk.Models;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Provisioning;
using Kontena.Sdk.Tooling;
using Kontena.Core.Models;
using Kontena.Core.Versioning;
using Kontena.Engines;
using Kontena.Engines.Plugins;

namespace Kontena.App.ViewModels;

/// <summary>
/// Connecting to a backend and moving between them: first launch, onboarding, probing, the
/// switcher's list, and the engine-down state when none of it worked.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>
    /// How long the shell waits for the probe round before carrying on without the stragglers
    /// (KON-357).
    /// <para>
    /// Every backend gets its own deadline to answer in, up to ten seconds for something across a
    /// network (KON-327, KON-329), and that is right for the backend — but the round is awaited as a
    /// whole, so one remote nobody can reach held the entire startup for its full deadline. Measured:
    /// a single unreachable engine took the shell from 3.1 to 13.2 seconds, on a machine where
    /// everything else answered in under a second.
    /// </para>
    /// <para>
    /// Two seconds rather than something tighter, because the round is not only a question — it is
    /// where the HTTP and Kubernetes stacks are first used, in parallel, and connecting afterwards
    /// reuses all of it. Cutting it short costs more than it saves: opening the cluster took 800 ms
    /// longer when the connect had to warm that up on its own. A healthy round finishes well inside
    /// this, so nothing changes for anyone whose backends answer.
    /// </para>
    /// </summary>
    internal static readonly TimeSpan ProbeRoundGrace = TimeSpan.FromSeconds(2);

    private async Task InitAsync()
    {
        try
        {
            // Started together, waited for apart: the backend being opened gets its own deadline, the
            // rest get whatever is left of the grace window and land in the switcher when they answer.
            var round = _registry.Providers.Select(p => BackendRegistry.ProbeAsync(p)).ToList();
            var all = Task.WhenAll(round);
            var target = StartupProbe(round);

            if (target is null)
            {
                // Nothing to open yet, so nothing to be early for: the wizard lists the engines that
                // answered, and picking "the first engine that answers" (KON-98) has to see them all
                // before it can call one first. Carrying on early here would offer a choice missing
                // whichever backend was merely slow.
                _probes = await Diag.TimeAsync("probe every backend", all);
            }
            else
            {
                await Diag.TimeAsync("probe every backend", Task.WhenAny(all, Task.Delay(_probeGrace)));

                // Whatever the window says, the one being opened is waited for: a target still in
                // flight reads as one that did not answer, and that is the "is gone" card over a
                // healthy cluster.
                await Diag.TimeAsync("wait for the one we want", target);

                _probes = [.. round.Where(t => t.IsCompletedSuccessfully).Select(t => t.Result)];

                if (!all.IsCompleted)
                {
                    Diag.Mark($"carrying on without {round.Count - _probes.Count} probe(s) still out");
                    _ = FinishRoundAsync(all, round);
                }
            }

            Diag.Time("build the settings page", BuildSettingsPage);
            RebuildEngineList();
            RefreshNewClusters();

            // Deliberately after the switcher is drawn and deliberately not awaited: this is the one
            // thing here that needs the network, and a list that waited for it would take as long as
            // the slowest lookup to show what is already known (the same shape KON-153 settled on).
            _ = RefreshSupportAsync();

            if (!_settings.Onboarded)
            {
                EnterOnboarding();
                return;
            }

            await Diag.TimeAsync("connect", ConnectPreferredAsync());
            Diag.Mark("shell usable");

            // After the shell is usable, never before: a slow or unreachable update server must not
            // hold up connecting to an engine, which is what the user actually opened Kontena for.
            _ = Update.CheckAsync();

            AskPluginConsent();
        }
        catch (Exception ex)
        {
            EnterBackendDown("Can't reach a container engine", ex.Message);
        }
    }

    /// <summary>
    /// The probe for the backend this launch is going to open, or null where nothing is owed a wait of
    /// its own: a first run (the wizard lists whatever answered), no remembered target, a target that
    /// is no longer a provider — its own message, rather than a silent landing somewhere else — or the
    /// screenshot harness, which picks by connectedness.
    /// </summary>
    private Task<BackendProbe>? StartupProbe(IReadOnlyList<Task<BackendProbe>> round)
    {
        if (!_settings.Onboarded || Environment.GetEnvironmentVariable("KONTENA_SCREENSHOT") == "1")
            return null;

        if (_settings.StartupTarget is not { Length: > 0 } target)
            return null;

        var index = _registry.Providers.ToList().FindIndex(p => p.Backend == target);
        return index < 0 ? null : round[index];
    }

    /// <summary>
    /// Let the stragglers finish behind the open shell and put them where a probe belongs: the
    /// switcher, the settings page, the "new clusters" row. Started from the UI thread, so all of that
    /// lands back on it.
    /// <para>
    /// Nothing is torn down and nothing is retried on failure — a backend that never answered keeps
    /// the "Not connected" it already has.
    /// </para>
    /// </summary>
    private async Task FinishRoundAsync(Task all, IReadOnlyList<Task<BackendProbe>> round)
    {
        try
        {
            await all;
        }
        catch (Exception)
        {
            // ProbeAsync answers rather than throws, so this is the unlikely half. Whatever did
            // answer is still worth showing.
        }

        var late = round.Where(t => t.IsCompletedSuccessfully).Select(t => t.Result).ToList();
        if (late.Count <= _probes.Count)
            return;

        Diag.Mark($"late probes in: {late.Count - _probes.Count} more");
        _probes = late;
        BuildSettingsPage();
        RebuildEngineList();
        RefreshNewClusters();
    }
    /// <summary>
    /// Ask about a plugin that was found but never agreed to (KON-279). Startup only loads what already
    /// has consent, so this is where a newly dropped-in plugin gets its answer — after the window
    /// exists, because a modal before there is one is not a thing Avalonia does gracefully.
    /// <para>
    /// One at a time: the shell has a single modal slot, and a second request would overwrite the first
    /// unasked. The next one comes up on the next launch. Cheap, and there is one plugin to install.
    /// </para>
    /// </summary>
    internal void AskPluginConsent()
    {
        // Both conditions guard the same thing — arbitrary code — so neither is redundant: Status is
        // the snapshot taken at the last Discover(), _settings is updated the moment the user answers.
        // Checking both is what stops a reconnect from re-asking about something already approved this
        // session, without waiting for _plugins to catch up.
        var pending = _plugins.FirstOrDefault(p =>
            p.Status == PluginStatus.AwaitingConsent && p.Manifest is not null
            && !_settings.AllowsPlugin(p.Manifest.Id, p.Manifest.Version, p.Sha256));

        if (pending?.Manifest is not { } manifest)
            return;

        // A plugin whose id and version were answered before, presenting bytes that answer did not
        // cover, is not a new plugin — it is one that changed underneath the answer (KON-362). Asking
        // that as "we found something in your folder" would leave out the only part worth interrupting
        // for: this is not what you allowed.
        var changed = _settings.KnowsPlugin(manifest.Id, manifest.Version);

        ShowConfirm(new ConfirmRequest(
            Title: changed ? "This plugin has changed — run it?" : "Run this plugin?",
            Message: changed
                ? $"{manifest.Name} is not the build you allowed: the same version, different code. "
                  + "That happens when you reinstall it, and it happens when something replaces it. It "
                  + "runs inside Kontena with the same access you have — only allow it if you changed "
                  + "it yourself."
                : $"{manifest.Name} was found in your plugins folder. It runs inside Kontena with "
                  + "the same access you have. Only allow it if you put it there.",
            ConfirmLabel: "Allow",
            // Nothing is destroyed here. The question is whether to trust, and the danger styling
            // would answer a different one.
            Destructive: false,
            Details:
            [
                new ConfirmDetail("IconPlug", manifest.Name, $"{manifest.Id} · {manifest.Version}"),
                new ConfirmDetail("IconInfo", "Published by", manifest.Author),
                new ConfirmDetail("IconFolder", "Loaded from", pending.Directory),
                // Rendered, never composed (KON-296): these are the author's own words about what the
                // plugin will do. Nothing here enforces them — an in-process plugin can do whatever this
                // app can — which is why they are shown as a claim, beside who made the claim.
                .. manifest.Permissions.Select(p => new ConfirmDetail("IconCheck", "Says it will", p)),
            ],
            OnConfirm: async () =>
            {
                // The digest from the scan the user was just shown, not one taken again here: rehashing
                // at this point would record whatever is on disk now, which is not necessarily what the
                // dialog described.
                var sha = pending.Sha256;
                var stored = _store.Update(s => s.WithAllowedPlugin(manifest.Id, manifest.Version, sha));
                _settings = _settings.WithAllowedPlugin(manifest.Id, manifest.Version, sha);

                // Load again rather than reaching into the loader for this one directory: the same call
                // that ran at startup now sees the consent, and there is one path by which a plugin
                // becomes a provider.
                var loaded = PluginLoader.Discover(
                    _pluginRoot,
                    c => stored.AllowsPlugin(c.Manifest.Id, c.Manifest.Version, c.Sha256));

                // Replace the snapshot, not just the providers: this plugin's entry is now Loaded
                // rather than AwaitingConsent, so a later reconnect's InitAsync (which reuses _plugins,
                // not a fresh scan) does not ask about it again or hand it to another PluginLoadContext.
                _plugins = loaded;

                BackendCatalog.SetPluginProviders(loaded.SelectMany(p => p.Providers));
                await ReloadBackendsAsync(BackendCatalog.ShouldIncludeDemo(_settings.ShowDemoBackends));
            }));
    }
    /// <summary>
    /// Open what the user was last on, or pinned, or — failing both — the first engine that answers
    /// (KON-98).
    /// <para>
    /// Auto-connect used to be engine-only, because entering a cluster swaps the whole UI mode and
    /// that should be a choice. It still is a choice: the app only returns to a cluster because the
    /// user picked it last time. What it must not do is pick one on its own, so a *fallback* is
    /// still an engine.
    /// </para>
    /// </summary>
    private async Task ConnectPreferredAsync()
    {
        // The screenshot renderer boots straight into its (single) demo provider, whatever
        // identity it presents, so captures never depend on a real Docker/Podman socket.
        if (Environment.GetEnvironmentVariable("KONTENA_SCREENSHOT") == "1")
        {
            var demo = _probes.FirstOrDefault(p => p.Connected) ?? (_probes.Count > 0 ? _probes[0] : null);
            if (demo is not null) { await ActivateAsync(demo.Provider); return; }
        }

        if (_settings.StartupTarget is { Length: > 0 } target)
        {
            var wanted = _probes.FirstOrDefault(p => p.Provider.Backend == target);

            if (wanted is null)
            {
                // Kube-context removed, engine uninstalled, demo backends switched off. Forget it
                // rather than offering a reconnect that can never succeed, and say so — silently
                // landing somewhere else is how you end up acting on the wrong cluster.
                _settings = _store.Update(s => s with
                {
                    LastBackend = null, PinnedBackend = null, Startup = StartupBackend.LastUsed,
                });
                BuildSettingsPage();

                EnterBackendDown(
                    $"{Pretty(target)} is gone",
                    $"Kontena last opened {Pretty(target)}, and it is no longer available — a kube-context may have been removed, or an engine uninstalled. Pick one below to carry on.");
                return;
            }

            if (wanted.Connected)
            {
                await ActivateAsync(wanted.Provider);
                return;
            }

            EnterBackendDown(
                $"Can't reach {NameOf(wanted.Provider)}",
                Unreachable(wanted),
                wanted);
            return;
        }

        var real = _probes.FirstOrDefault(p =>
            p.Connected && p.Provider.Kind == BackendKind.Engine && p.Provider.Backend != FakeBackend);

        if (real is null)
        {
            // Two different situations wear the same "nothing connected" (KON-255). Telling someone
            // with no engine installed that theirs "may be stopped or still starting" sends them
            // looking for a daemon that was never there — and since that machine's switcher is now
            // empty rather than full of dead rows, this text is the only thing left saying why.
            var anyInstalled = _probes.Any(p =>
                p.Provider.Kind == BackendKind.Engine
                && p.Provider.Backend != FakeBackend
                && p.Provider.IsInstalled);

            EnterBackendDown(
                anyInstalled ? "Can't reach a container engine" : "No container engine found",
                anyInstalled
                    ? "No Docker or Podman socket answered. The engine may be stopped, still starting, or you may not have permission to access it."
                    : "Kontena found no sign of Docker or Podman on this machine. Install one, or add an engine on another host from Settings.",
                UnreachablePodmanProbe());
            return;
        }

        await ActivateAsync(real.Provider);
    }
    /// <summary>Why a known backend did not answer, in terms that fit what it is.</summary>
    private string Unreachable(BackendProbe probe) => probe.Provider.Kind == BackendKind.Cluster
        ? $"The apiserver for {NameOf(probe.Provider)} did not answer. The cluster may be stopped, unreachable from this network, or your credentials may have expired."
        : $"The {NameOf(probe.Provider)} socket did not answer. It may be stopped, still starting, or you may not have permission to access it.";
    /// <summary>The probe behind a "no engine answered" message, when it was Podman that failed —
    /// the one case there is a specific fix to check for.</summary>
    private BackendProbe? UnreachablePodmanProbe() =>
        _probes.FirstOrDefault(p => p.Provider.Backend == "podman" && !p.Connected);
    /// <summary>What this backend is called here — the user's name for it, or the source's own (KON-119).</summary>
    private string NameOf(IBackendProvider provider) =>
        _settings.NameFor(provider.Backend, provider.DisplayName);
    /// <summary>
    /// Picks up a rename without reconnecting anything. Also drops names for backends that are gone, so
    /// the settings file does not accumulate an entry for every cluster the user ever saw.
    /// </summary>
    private void RefreshBackendNames()
    {
        _settings = _store.Update(s => s.PruneBackendNames(_registry.Providers.Select(p => p.Backend)));

        RebuildEngineList();

        if (_probes.FirstOrDefault(p => p.Provider.Backend == _activeBackend)?.Provider is { } active)
            EngineName = NameOf(active);
    }
    /// <summary>
    /// A backend id read back as something a person recognises. Ids are namespaced
    /// (<c>kubernetes:kind-kind</c>) and the context half is the part the user named.
    /// </summary>
    private static string Pretty(string backend) =>
        backend.Split(':') is [_, var context] && context.Length > 0 ? context : backend;
    /// <param name="autoDetect">
    /// The toggle to open with. Passed on a rescan, which builds a fresh view model: without it the
    /// switch would silently spring back to the stored value every time the user probed again.
    /// </param>
    private void EnterOnboarding(bool? autoDetect = null)
    {
        IsReady = false;
        IsBackendDown = false;
        CurrentPage = null;

        // What is ticked on the screen being replaced, if one is (KON-351). A rescan builds a fresh
        // wizard, and nothing is written down until Continue — so a cluster the user unticked is still
        // "never offered" as far as the settings are concerned, and would come back ticked. The wizard
        // rescans itself after starting an engine (KON-335), so this is not a rare path: the user
        // unticks two clusters, lets Kontena start Podman, and continues with all four.
        var ticked = Onboarding?.Clusters
            .ToDictionary(c => c.Backend, c => c.IsSelected, StringComparer.Ordinal);
        Onboarding = new OnboardingViewModel(
            _probes.Where(p => p.Provider.Kind == BackendKind.Engine).ToList(),
            FakeBackend,
            autoDetect ?? _settings.AutoDetectEngines,
            onContinue: backend => _ = CompleteOnboardingAsync(backend),
            onSkip: () => _ = CompleteOnboardingAsync(null),
            onInstallPodman: () => Browser.OpenUrl("https://podman.io/docs/installation"),
            onRescan: RunSetupAsync,
            onStartEngine: StartWizardEngineAsync,
            nameOf: NameOf,
            // Read from the kubeconfig, not from the probes: which clusters are providers at all is
            // itself the answer this screen is asking for, so at first run there are none (KON-336).
            clusters: BackendCatalog.DiscoverClusters(_settings.KubeconfigPaths),
            // New arrives ticked, declined comes back unticked rather than hidden — the same three
            // states the switcher's "new clusters" row keeps (KON-120). An answer already given on the
            // screen this one replaces outranks all of that: it is newer than what is stored.
            clusterTicked: id => ticked is not null && ticked.TryGetValue(id, out var chosen)
                ? chosen
                : _settings.ShowsCluster(id) || _settings.NewClusters([id]).Count > 0);
        IsOnboarding = true;

        _ = OfferWizardEngineStartAsync(Onboarding);
    }
    /// <summary>
    /// Offer to start the engine the wizard is waiting on, when there is a checked fix for it
    /// (KON-335). The same <see cref="PodmanSocketFix"/> the engine-down card uses: `podman ps` works
    /// from a terminal but the API socket answers nothing, because the user socket unit was never
    /// enabled.
    /// <para>
    /// It belongs here more than on the down card. The wizard is where a first run meets a stopped
    /// engine, it is the screen that asks you to start one, and until now it was the screen with no
    /// way to do it — the error card offered more help than the screen meant to prevent the error.
    /// </para>
    /// </summary>
    private async Task OfferWizardEngineStartAsync(OnboardingViewModel wizard)
    {
        if (!_probes.Any(p => p.Provider.Backend == "podman" && !p.Connected))
            return;

        if (!await PodmanSocketFix.IsFixableAsync(_toolRunner))
            return;

        // A rescan replaces the view model, and the user may have left the wizard entirely while
        // systemd was being asked. Offering the fix on a screen that is gone would do nothing; worse,
        // it would keep a stale wizard alive.
        if (IsOnboarding && ReferenceEquals(Onboarding, wizard))
            wizard.FixCommandLine = PodmanSocketFix.EnableSocket.CommandLine;
    }
    /// <summary>
    /// Runs that fix, then rescans so the row goes from "Not running" to selectable in place. On
    /// failure it says what went wrong and leaves the screen alone: a rescan would only redraw the
    /// same stopped engine and read as if nothing had been tried.
    /// </summary>
    private async Task StartWizardEngineAsync()
    {
        var wizard = Onboarding;
        if (wizard is null)
            return;

        try
        {
            var result = await _toolRunner.RunAsync(PodmanSocketFix.EnableSocket);
            if (result.Ok)
            {
                await RunSetupAsync();
                return;
            }

            wizard.FixError = $"Starting it failed: {result.Complaint}";
        }
        catch (Exception ex)
        {
            wizard.FixError = $"Starting it failed: {ex.Message}";
        }
    }
    /// <summary>
    /// Probe again and hand the first-run wizard back, whether or not it has run before.
    /// <para>
    /// Reached from the engine-down card and from the wizard's own rescan. Skipping used to be a
    /// one-way door: <c>Onboarded</c> is a latch, so the app went on picking the first engine that
    /// answered and never asked again — fine when there is one engine, silent when there are two.
    /// Reconnect restores the connection but not the choice, which is what this restores.
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task RunSetupAsync()
    {
        _probes = await _registry.ProbeAllAsync();
        RebuildEngineList();
        RefreshNewClusters();
        EnterOnboarding(Onboarding?.AutoDetect);
    }
    private async Task CompleteOnboardingAsync(string? backend)
    {
        var autoDetect = Onboarding?.AutoDetect ?? _settings.AutoDetectEngines;

        // Only on Continue. Skipping is "not now", and writing a decline for every context would turn
        // it into "never", which is the one thing the three states exist to avoid.
        var clusters = backend is null ? [] : Onboarding?.Clusters ?? [];

        // Onboarding no longer pins: picking an engine here says "start me here", not "and never
        // follow me anywhere else". Activating it records it as last used, which is enough.
        _settings = _store.Update(s =>
        {
            // ClusterChoiceOffered whether they answered or skipped: the question has been put, so the
            // one-time adoption at startup must stop treating this install as one that predates it
            // (KON-351). Skip still writes no answers — "not now" keeps the contexts new.
            var next = s with
            {
                Onboarded = true, AutoDetectEngines = autoDetect, ClusterChoiceOffered = true,
            };

            // Both answers are recorded, not just yes: a cluster ticked off here is declined, and a
            // declined cluster must not be offered again on every launch (KON-120).
            foreach (var cluster in clusters)
                next = next.WithCluster(cluster.Backend, cluster.IsSelected);

            return next;
        });
        BuildSettingsPage(); // reflect the just-chosen default in Settings

        IsOnboarding = false;
        Onboarding = null;

        // The chosen clusters have no provider yet — the catalog was built before anyone said which
        // ones belong in the switcher. Rebuilding is what makes the choice real.
        if (clusters.Any(c => c.IsSelected))
            await RebuildBackendsAsync(
                BackendCatalog.ShouldIncludeDemo(_settings.ShowDemoBackends), _settings);

        if (backend is not null)
        {
            var provider = _probes.FirstOrDefault(p => p.Provider.Backend == backend && p.Connected)?.Provider;
            if (provider is not null)
            {
                await ActivateAsync(provider);
                return;
            }
        }

        await ConnectPreferredAsync();
    }
    /// <summary>Which backend the down card is currently about, so a fix suggestion that resolves
    /// after the user has already moved on (reconnected, switched) knows to keep quiet.</summary>
    private string? _backendDownFor;

    private void EnterBackendDown(string title, string detail, BackendProbe? probe = null)
    {
        IsReady = false;
        IsBackendDown = true;
        BackendDownTitle = title;
        BackendDownDetail = detail;
        BackendDownFixCommand = null;
        _backendDownFor = probe?.Provider.Backend;
        IsClusterMode = false;
        EngineName = "Not connected";
        EngineChip = new BackendChipInfo("!");
        EngineDetail = "not connected";
        EngineEndpoint = string.Empty;
        EngineSupport = null;
        CurrentPage = null;
        OnPropertyChanged(nameof(HasAlternatives));

        if (probe is { Connected: false, Provider.Backend: "podman" })
            _ = SuggestPodmanFixAsync(probe.Provider.Backend);
    }
    /// <summary>
    /// Checked after the down card is already showing — asking systemctl takes a moment, and the
    /// reason Podman is unreachable should never hold up saying that it is.
    /// </summary>
    private async Task SuggestPodmanFixAsync(string backend)
    {
        if (!await PodmanSocketFix.IsFixableAsync(_toolRunner))
            return;

        // The user may have reconnected, or switched to something else, while this was running.
        if (IsBackendDown && _backendDownFor == backend)
            BackendDownFixCommand = PodmanSocketFix.EnableSocket.CommandLine;
    }
    /// <summary>The suggested fix command for the current down state, or null when there is none —
    /// binds the "Run it" / "Copy" row in the down card.</summary>
    [ObservableProperty] private string? _backendDownFixCommand;
    /// <summary>True while <see cref="ApplyBackendFixAsync"/> is running.</summary>
    [ObservableProperty] private bool _isFixingBackend;
    /// <summary>
    /// Runs the suggested fix on explicit request — never on its own. `systemctl --user enable --now
    /// podman.socket` needs no elevation (it manages a user unit), so there is no password prompt to
    /// wire up here; a system-wide fix would need one and does not exist yet.
    /// </summary>
    [RelayCommand]
    private async Task ApplyBackendFixAsync()
    {
        if (BackendDownFixCommand is null || IsFixingBackend)
            return;

        IsFixingBackend = true;
        try
        {
            var result = await _toolRunner.RunAsync(PodmanSocketFix.EnableSocket);
            if (result.Ok)
                await ReconnectAsync();
            else
                BackendDownDetail = $"{BackendDownDetail} Running the command failed: {result.Complaint}";
        }
        catch (Exception ex)
        {
            BackendDownDetail = $"{BackendDownDetail} Running the command failed: {ex.Message}";
        }
        finally
        {
            IsFixingBackend = false;
        }
    }
    [RelayCommand]
    private async Task ReconnectAsync()
    {
        IsBackendDown = false;
        BackendDownDetail = string.Empty;
        await InitAsync();
    }
    private async Task ActivateAsync(IBackendProvider provider)
    {
        Containers?.StopWatching();
        _activityLog.Detach();
        await StopPortForwardsAsync();
        StopFollowingNamespaces();
        (_engine as IDisposable)?.Dispose();
        (_cluster as IDisposable)?.Dispose();
        _engine = null;
        _cluster = null;

        IsReady = false;
        IsBackendDown = false;

        var backend = provider.CreateBackend();
        _activeBackend = provider.Backend;
        EngineName = NameOf(provider);
        EngineChip = BackendChipInfo.For(provider);

        // Said before the wait rather than after it (KON-375). Everything below this line is the wait.
        ConnectingMessage = provider.Kind == BackendKind.Cluster
            ? $"Opening {EngineName}…"
            : $"Connecting to {EngineName}…";

        RebuildEngineList();
        CloseDetail();
        CloseDialog();

        if (backend is IClusterEngine cluster)
        {
            if (!await Diag.TimeAsync("open the cluster", EnterClusterModeAsync(cluster)))
                return;
        }
        else if (backend is IContainerEngine engine)
        {
            await Diag.TimeAsync("open the engine", EnterEngineModeAsync(engine));
        }
        else
        {
            // A provider that is neither axis has nothing to show. Say so rather than leaving a
            // blank shell behind — and above all, do not remember it as somewhere worth returning to.
            EnterBackendDown(
                $"Can't open {NameOf(provider)}",
                "This backend is neither a container engine nor a cluster, so Kontena has nothing to show for it.");
            return;
        }

        Remember(provider.Backend);
    }
    /// <summary>
    /// Record what is open so the next launch can return to it (KON-98). Written only after the
    /// backend actually came up — remembering something that failed would reopen the failure.
    /// </summary>
    private void Remember(string backend)
    {
        if (_settings.LastBackend == backend)
            return;

        _settings = _store.Update(s => s with { LastBackend = backend });
    }
    /// <summary>
    /// Open a container engine and land on Containers.
    /// <para>
    /// Internal rather than private for the same reason as <see cref="EnterClusterModeAsync"/>: what
    /// landing does to the shell — the nav, the history — only happens here, so a test of it has to
    /// come through this door too.
    /// </para>
    /// </summary>
    internal async Task EnterEngineModeAsync(IContainerEngine engine)
    {
        _engine = engine;
        IsClusterMode = false;
        // A different backend is a different world with its own nav; carrying the stack across would
        // offer a Back to a page this side has no menu entry for (KON-173).
        ClearHistory();
        SetEngineNav();

        Containers = new ContainersViewModel(_engine)
        {
            RequestOpenDetail = ShowContainerDetail,
            RequestRunContainer = image => _ = ShowRunDialogAsync(image),
            RequestPullImage = ShowPullDialog,
            RequestConfirm = ShowConfirm,
            RequestMigrateContainer = id => _ = ShowMigrateDialogAsync(id),

            // Nothing to migrate to on a machine with one engine, so the action is not offered there
            // rather than offered and then refused.
            HasMigrationTargets = _registry.Providers.Count(p => p.Kind is BackendKind.Engine) > 1,

            // Grouping is remembered per backend (KON-159); the page owns the choice, the shell owns
            // where it is kept.
            LoadGrouping = () => _settings.GroupsContainers(_activeBackend),
            SaveGrouping = grouped =>
                _settings = _store.Update(s => s.WithContainerGrouping(_activeBackend, grouped)),

            // The group row links to the other half of Compose rather than duplicating it.
            RequestOpenProject = ShowProject,
        };
        Images = new ImagesViewModel(_engine)
        {
            RequestPullImage = ShowPullDialog,
            RequestBuildImage = ShowBuildDialog,
            RequestTagPushImage = ShowTagPushDialog,
            RequestConfirm = ShowConfirm,
        };
        Volumes = new VolumesViewModel(_engine)
        {
            RequestCreateVolume = ShowCreateVolumeDialog,
            RequestBrowseVolume = ShowBrowseVolumeDialog,
            RequestConfirm = ShowConfirm,
        };
        Networks = new NetworksViewModel(_engine)
        {
            RequestCreateNetwork = ShowCreateNetworkDialog,
            RequestNetworkAttachments = ShowNetworkAttachmentsDialog,
            RequestConfirm = ShowConfirm,
        };
        ComposeProjects = new ComposeProjectsViewModel(_engine)
        {
            RequestOpenDetail = ShowContainerDetail,
            RequestNewProject = ShowComposeUpDialog,
            RequestProjectLogs = ShowComposeLogsDialog,
            RequestConfirm = ShowConfirm,
        };
        SearchText = string.Empty;

        await Containers.LoadAsync();

        // Through the same door as every other navigation (KON-263). Landing here used to set
        // CurrentPage directly, so the shell arrived somewhere without recording that it had — and
        // the first Back of the session had nothing behind it.
        //
        // After the load rather than before: Navigate starts one for a page that has not loaded yet,
        // and this one is already under way. Nothing renders CurrentPage until IsReady anyway.
        Navigate("containers");

        IsReady = true;

        // The badges follow the engine's events too (KON-339). Containers is the only engine page
        // that watches, and the count it moves is not only its own: a Compose project appears when
        // its first container does.
        Containers.Changed = () => _ = RefreshNavCountsAsync();
        Containers.StartWatching();
        _activityLog.Attach(_engine, _activeBackend, ResolveEventName);

        await UpdateNavCountsAsync();
    }
    /// <summary>
    /// Returns false when the cluster could not be opened and the down state took over.
    /// <para>
    /// Internal rather than private so the nav tests can put the shell in cluster mode without a
    /// kubeconfig and a registry — the namespace switch is a shell behaviour and only reproduces here
    /// (KON-200).
    /// </para>
    /// </summary>
    internal async Task<bool> EnterClusterModeAsync(IClusterEngine cluster)
    {
        _cluster = cluster;
        IsClusterMode = true;
        ClearHistory();
        SetClusterNav();

        // The registry probes a throwaway instance, so this one has never been contacted. Ping it:
        // adapters settle capabilities that are only knowable once connected (which metrics source
        // answered, say), and the pages below read those.
        try
        {
            await cluster.PingAsync();
        }
        catch (Exception ex)
        {
            // This used to be swallowed, on the theory that the listers would simply report nothing.
            // They do — and the result was a fully-drawn cluster UI with every grid empty and no hint
            // that the reason was an expired token. A cluster that cannot be reached is a state, not
            // an absence of data.
            _cluster = null;
            (cluster as IDisposable)?.Dispose();
            EnterBackendDown($"Can't reach {EngineName}", Explain(ex));
            return false;
        }

        // The engine-only pages don't apply in cluster mode.
        Containers = null;
        Images = null;
        Volumes = null;
        Networks = null;
        ComposeProjects = null;
        Activity = null;

        // The ping answered, so the promise this makes can be kept: offering to reopen tunnels on a
        // cluster we cannot reach would be an empty one (KON-105).
        RestorePortForwards(cluster, _activeBackend);

        // Everything a page is built from, before the first page is built (KON-375).
        //
        // This used to run the other way round, and it cost the whole open. The picker was filled by
        // hand here, the overview was built, and then selecting a namespace read the workload kinds
        // and rebuilt the page — because which page Workloads is depends on those kinds (KON-200).
        // So one open listed the namespaces six times, and built the landing page twice: six reads
        // and seven watch streams opened, torn down, and started again, with nobody ever seeing the
        // first set of answers. On a remote cluster every one of those is a round-trip, and they
        // compete with each other for the same connection pool — which is most of what "fetching a
        // cluster feels slow" was.
        Namespaces.Clear();
        Namespaces.Add(AllNamespaces);

        // The field rather than the property: the change handler's job is to rebuild the open page,
        // and there is no page yet. Announced below, once the picker behind it holds real names.
        _selectedNamespace = AllNamespaces;

        // The picker's one read. From here it is kept in step by its own watch rather than by being
        // re-read in front of every navigation (KON-396) — so this is the read, not the first of many.
        await ReadNamespacesAsync();
        FollowNamespaces();

        // Fills the Workloads submenu.
        await UpdateClusterNavAsync();
        OnPropertyChanged(nameof(SelectedNamespace));

        SearchText = string.Empty;

        // Same door, same reason (KON-263). This side had the identical gap: the overview was built
        // here rather than navigated to, so a cluster's first Back was missing too. Without the
        // sidebar refresh it normally brings, since the line above is that read.
        NavigateTo("overview", refreshNav: false);

        IsReady = true;
        return true;
    }
    /// <summary>
    /// The adapter's own words where it has them. Adapters map their failures onto Kontena's
    /// exception types, so "expired token" and "cluster is off" arrive here already distinguished.
    /// </summary>
    private static string Explain(Exception ex) => ex switch
    {
        EngineUnreachableException or EnginePermissionException => ex.Message,
        EngineException => ex.Message,
        _ => $"{ex.Message} Try again, or pick another backend below.",
    };
    /// <summary>Best-effort friendly name for an event's resource, from the loaded container list.</summary>
    private string? ResolveEventName(EngineEvent ev)
    {
        if (ev.ResourceKind != ResourceKind.Container)
            return null;

        // Containers only: the list also holds Compose headings now (KON-159), and a group has no id
        // an engine event could be about.
        return Containers?.Items.OfType<ContainerRowViewModel>().FirstOrDefault(c =>
            c.Id == ev.ResourceId
            || c.Id.StartsWith(ev.ResourceId, StringComparison.Ordinal)
            || ev.ResourceId.StartsWith(c.Id, StringComparison.Ordinal))?.Name;
    }
    private void BuildSettingsPage()
    {
        // Rebuilding replaces the instance, and CurrentPage holds the old one by reference. Left
        // alone, someone standing on Settings when a rebuild happens — flipping the demo toggle
        // does exactly that — would be looking at a page the shell no longer considers shown
        // (KON-137).
        var wasShowing = SettingsPage is not null && ReferenceEquals(CurrentPage, SettingsPage);

        // …and the category with it. A rebuild happens for reasons that have nothing to do with where
        // the user is standing — the demo toggle, a kubeconfig, a cluster being created — and dropping
        // them back on General each time is the shell losing their place.
        var category = SettingsPage?.Category;

        // Which of these are remotes the user configured decides whether the row can point at its own
        // entry further down the page (KON-264). Read from settings rather than from the backend id's
        // shape: the id format is the remote adapter's business, not this list's.
        var remoteBackends = _settings.RemoteEngines.Select(r => r.Backend).ToHashSet(StringComparer.Ordinal);

        var all = _probes.Select(p => new EngineListItem(
            p.Provider.Backend, NameOf(p.Provider), BackendChipInfo.For(p.Provider),
            p.Detail ?? string.Empty, p.Connected,
            p.Provider.Backend == _settings.ResolvedPinnedBackend,
            p.Provider.DisplayName,
            IsRemote: remoteBackends.Contains(p.Provider.Backend))).ToList();

        // The detected-engines list stays engine-only; what you can pin does not — a cluster is a
        // perfectly reasonable thing to always start on.
        var engines = all
            .Where(e => _probes.First(p => p.Provider.Backend == e.Backend).Provider.Kind == BackendKind.Engine)
            .ToList();

        SettingsPage = new SettingsViewModel(_store, _settings, engines, new SettingsContext
        {
            Backends = all,
            OnDemoBackendsChanged = ReloadBackendsAsync,
            Update = Update,
            Secrets = _secrets,
            Registries = _registryCredentials,
            Engine = () => _engine,

            // Adding or removing a remote changes the provider list, which is what the switcher is built
            // from — so the same rebuild the demo toggle uses (KON-46).
            OnRemotesChanged = () => ReloadBackendsAsync(BackendCatalog.ShouldIncludeDemo(_settings.ShowDemoBackends)),

            // A rename changes no connection, so it must not cost a re-probe: re-read the names and
            // redraw. Probing on every keystroke would make typing a name feel like a reconnect.
            OnNamesChanged = RefreshBackendNames,

            // Settings retries in place: it re-probes and shows the answer, but does not switch. Someone
            // fixing a connection from this page is not asking to be taken out of it — that choice is
            // the switcher's, where clicking a row means "open this" (KON-328).
            RetryBackend = ReprobeAsync,

            // Every cluster in every kubeconfig, not only the chosen ones — the hidden ones are exactly
            // what this list is for (KON-120).
            Clusters = DiscoveredClusters(),
            OnClustersChanged = () =>
                ReloadBackendsAsync(BackendCatalog.ShouldIncludeDemo(_settings.ShowDemoBackends)),
            Kubeconfigs = Kubeconfigs(),
        })
        {
            // Local clusters (KON-109 + KON-76) — the one page that outlives its settings page.
            LocalClusters = _localClusters ??= BuildLocalClustersPage(),
            RemoteClusters = _remoteClusters ??= BuildProvisioningWizard(),

            // A changed shortcut has to reach the window's binding collection, or it would only take
            // effect on the next launch (KON-180).
            RequestShortcutsChanged = ShortcutsChanged,
        };

        SettingsPage.RequestConfirm = ShowConfirm;

        if (category is not null)
            SettingsPage.Category = category;

        if (wasShowing)
            CurrentPage = SettingsPage;
    }

    /// <summary>
    /// The local-clusters page (KON-76), built once and kept across settings rebuilds.
    /// <para>
    /// Kept, because creating a cluster <i>causes</i> a rebuild: the new kubeconfig context has to
    /// reach the switcher. Handing the user a fresh page halfway through would throw away the console
    /// they are reading and leave the running create writing into a view model nobody can see.
    /// </para>
    /// </summary>
    private LocalClustersViewModel BuildLocalClustersPage() => new(
        tooling: new ClusterToolingViewModel
        {
            RequestOpenUrl = Browser.OpenUrl,
            RequestConfirm = ShowConfirm,
        })
    {
        RequestConfirm = ShowConfirm,

        // The provisioner never touches the registry (KON-78): it makes a cluster, kind writes the
        // kubeconfig context, and this rebuild is what notices.
        RequestClustersChanged = () =>
            ReloadBackendsAsync(BackendCatalog.ShouldIncludeDemo(_settings.ShowDemoBackends)),

        // KON-120 says clusters appear on choice — with one deliberate exception, this one. Having to
        // tick a box for the cluster you just made here is the dead-button mistake (KON-117) in a hat.
        RequestShowCluster = id => _settings = _store.Update(s => s.WithCluster(id, shown: true)),

        // Reports back whether the switch actually happened: a cluster whose control plane is still
        // settling will not be connected yet, and the page needs to know that to keep offering it.
        RequestUseBackend = async id =>
        {
            await SwitchEngineAsync(id);
            return _activeBackend == id;
        },

        ActiveBackendNow = () => _activeBackend,
    };

    /// <summary>
    /// The provisioning wizard (KON-379), built once and kept across settings rebuilds — for the same
    /// reason the local page is: a half-filled host table is work, and handing the user a fresh one
    /// because something unrelated changed throws it away.
    /// <para>
    /// The demo backends switch decides which provisioners it offers. With them on it gets KON-236's
    /// fake, which streams a rollout and touches nothing, and a preflight probe that answers from a
    /// script — so the whole flow is walkable, and screenshottable, on a machine with no fleet. With
    /// them off it gets the real k0s provisioner over real SSH.
    /// </para>
    /// </summary>
    private ProvisioningWizardViewModel BuildProvisioningWizard()
    {
        var demo = BackendCatalog.ShouldIncludeDemo(_settings.ShowDemoBackends);

        IRemoteClusterProvisioner provisioner = demo
            ? new FakeRemoteClusterProvisioner { DisplayName = "k0s (demo)" }
            : new K0sClusterProvisioner(new ToolRunner());

        var choice = new RemoteProvisionerChoiceViewModel(
            provisioner,
            "One k0sctl.yaml describes the whole cluster — machines, roles and network in one file — "
            + "and what comes out ships Autopilot, so it can upgrade itself later.");

        var wizard = new ProvisioningWizardViewModel(
            [choice],
            demo ? (host, _) => DemoProbe(host.Address) : null)
        {
            RequestConfirm = ShowConfirm,
        };

        // Not awaited: the page is built synchronously and the rows fill themselves in.
        _ = wizard.LoadAsync();

        return wizard;
    }

    /// <summary>
    /// A machine for the demo: healthy enough to reach the end, with its clock a few minutes out so
    /// there is a real warning on screen rather than a wall of green.
    /// <para>
    /// Deliberately not the swap failure, which is the one with a remedy: the canned answers do not
    /// change when a command runs, so "turn swap off" would report success and then find swap still
    /// on. A demo that cannot be completed is worse than one that shows fewer states, and the
    /// fix-and-check-again loop is covered by tests instead.
    /// </para>
    /// </summary>
    private static IPreflightProbe DemoProbe(string address)
    {
        var last = address.Split('.')[^1];

        return new FakePreflightProbe(address)
            .Answer("echo kontena-preflight", ProbeResult.Success("kontena-preflight"))
            .Answer("sudo -n true", ProbeResult.Success())
            .Answer("uname", ProbeResult.Success("Linux x86_64"))
            .Answer("ss -Hltn", ProbeResult.Success("LISTEN 0 128 0.0.0.0:22 0.0.0.0:*"))
            .Answer("swapon", ProbeResult.Success())
            .Answer("date +%s", ProbeResult.Success(
                DateTimeOffset.UtcNow.AddMinutes(3).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)))
            .Answer("hostname", ProbeResult.Success(
                $"node-{last}\n{Guid.NewGuid()}\naa:bb:cc:00:00:{last},"));
    }

    /// <summary>
    /// Rebuild the backend set after the demo toggle changed (KON-96), re-probe, and refresh the
    /// switcher. If the active backend was one of the ones that just went away, fall back to a
    /// connected real one rather than leaving a dead session on screen.
    /// </summary>
    private async Task ReloadBackendsAsync(bool includeDemo)
    {
        _settings = _settings with { ShowDemoBackends = includeDemo };
        var stored = _store.Load();

        // Prune here rather than only at startup: removing a kubeconfig takes its clusters with it, and
        // their names and visibility would otherwise linger until the next launch (KON-122).
        var known = BackendCatalog.DiscoverClusters(stored.KubeconfigPaths).Select(p => p.Backend).ToList();
        stored = _store.Update(s => s.PruneClusters(known)
            .PruneBackendNames([.. known, .. s.RemoteEngines.Select(r => r.Backend), "docker", "podman"]));

        await RebuildBackendsAsync(includeDemo, stored);
        BuildSettingsPage();

        if (_registry.Providers.Any(p => p.Backend == _activeBackend))
            return;

        var replacement = _probes.FirstOrDefault(p => p.Connected && p.Provider.Kind == BackendKind.Engine)
                          ?? _probes.FirstOrDefault(p => p.Connected);
        if (replacement is not null)
            await ActivateAsync(replacement.Provider);
        else
            EnterBackendDown(
                "No backend is reachable",
                "Nothing answered after the backend list changed. Start an engine, or turn the demo backends back on in Settings.",
                UnreachablePodmanProbe());
    }
    /// <summary>
    /// Build the provider set from what is stored now, probe it, and refresh the switcher. Shared by
    /// the demo toggle, a created cluster, and the first-run wizard — all three change <i>which</i>
    /// backends exist, which is the one thing the registry cannot notice by itself.
    /// </summary>
    private async Task RebuildBackendsAsync(bool includeDemo, KontenaSettings stored)
    {
        _registry.Replace(_buildCatalog(
            BackendCatalog.ShouldIncludeDemo(includeDemo),
            stored.RemoteEngines, stored.KubeconfigPaths, stored.ShowsCluster));
        BackendChips.Learn(_registry.Providers);
        _probes = await _registry.ProbeAllAsync();
        RefreshNewClusters();

        RebuildEngineList();
    }

    [RelayCommand]
    private async Task SwitchEngineAsync(string backend)
    {
        if (backend == _activeBackend)
            return;

        var probe = _probes.FirstOrDefault(p => p.Provider.Backend == backend && p.Connected);
        if (probe is not null)
            await ActivateAsync(probe.Provider);
    }

    /// <summary>The backend currently being re-probed, or null — the switcher row that was clicked says
    /// so rather than looking ignored while a remote takes its ten seconds.</summary>
    private string? _retryingBackend;

    /// <summary>
    /// Ask one backend again, and open it if it answers this time (KON-327/KON-328).
    /// <para>
    /// The probe result used to be a one-off cache with no reachable refresh: an engine that was still
    /// starting when Kontena launched stayed dead in the switcher for the rest of the session, and the
    /// only button that re-probed lived in the down card — which is not on screen when something else
    /// did connect. So the retry belongs on the row itself, where the user is standing when they notice.
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task RetryBackendAsync(string backend)
    {
        if (_retryingBackend is not null)
            return;

        _retryingBackend = backend;
        RebuildEngineList();
        try
        {
            if (!await ReprobeAsync(backend))
                return;
        }
        finally
        {
            _retryingBackend = null;
            RebuildEngineList();
        }

        await SwitchEngineAsync(backend);
    }

    /// <summary>
    /// Probe one provider again and fold the answer into the cached round, so the switcher and the
    /// Settings rows both stop describing a failure that is over. Returns whether it answered.
    /// </summary>
    private async Task<bool> ReprobeAsync(string backend)
    {
        if (_registry.Providers.FirstOrDefault(p => p.Backend == backend) is not { } provider)
            return false;

        var probe = await BackendRegistry.ProbeAsync(provider);
        _probes = [.. _probes.Select(p => p.Provider.Backend == backend ? probe : p)];

        RebuildEngineList();

        // The rows in place rather than a fresh Settings page: rebuilding it while the user is standing
        // on it would throw away a remote form half-typed next to the row they just retried.
        SettingsPage?.SetBackendConnected(backend, probe.Connected, probe.Detail ?? string.Empty);

        return probe.Connected;
    }
    /// <summary>
    /// Where release calendars are read from (KON-370). Null means nothing is said about any version.
    /// Set from the constructor, not by an object initializer: <c>InitAsync</c> starts during
    /// construction and would read an init property that had not been assigned yet.
    /// </summary>
    private VersionSupportCheck? Versions { get; }

    /// <summary>
    /// What each backend's publisher says about the version it reports, once that answer has arrived
    /// (KON-370). Kept here rather than on the row so <see cref="EngineOption"/> stays immutable — the
    /// list is rebuilt wholesale anyway, and an answer landing is just another reason to rebuild.
    /// </summary>
    private readonly Dictionary<string, VersionSupport> _support = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Fill in which backends run a release nobody maintains any more. Answers are cached for a day, so
    /// this is usually free; the first run of the day costs one lookup per distinct product. Offline it
    /// quietly finds nothing, which is the same as having nothing to say.
    /// </summary>
    private async Task RefreshSupportAsync(CancellationToken ct = default)
    {
        if (Versions is null)
            return;

        var now = DateTimeOffset.UtcNow;
        var landed = false;

        foreach (var probe in _probes.ToList())
        {
            if (ct.IsCancellationRequested)
                return;

            if (BackendProducts.For(probe.Provider.Backend, probe.Distribution) is not { } product)
                continue;

            if (await Versions.CheckAsync(product, probe.Version, now, ct) is not { } support)
                continue;

            _support[probe.Provider.Backend] = support;
            landed = true;
        }

        if (landed)
            RebuildEngineList();
    }

    private void RebuildEngineList()
    {
        // The same verdict the dropdown row carries, on the pill you are looking at anyway (KON-371).
        // Outside the loop below and keyed on the active backend rather than set from its row, because
        // the two things this needs arrive in either order: the support lookup is fired before the
        // preferred backend is opened, so the rebuild it triggers can run while nothing is active yet,
        // and the rebuild the open triggers can run before any answer has landed. Recomputing it on
        // every rebuild is right whichever way round they finish.
        EngineSupport = IsBackendDown ? null : _support.GetValueOrDefault(_activeBackend);

        Engines.Clear();
        Clusters.Clear();
        foreach (var probe in _probes)
        {
            var isActive = probe.Provider.Backend == _activeBackend;

            if (!BelongsInSwitcher(probe, isActive))
                continue;

            if (isActive)
            {
                // Detail is "{version} · {endpoint}" — split it so the endpoint sits on its own line.
                var detail = probe.Detail ?? string.Empty;
                var sep = detail.IndexOf(" · ", StringComparison.Ordinal);
                if (sep >= 0)
                {
                    EngineDetail = detail[..sep];
                    EngineEndpoint = detail[(sep + 3)..];
                }
                else
                {
                    EngineDetail = string.IsNullOrEmpty(detail) ? "engine" : detail;
                    EngineEndpoint = string.Empty;
                }
            }

            var option = new EngineOption
            {
                Backend = probe.Provider.Backend,
                Name = NameOf(probe.Provider),
                Chip = BackendChipInfo.For(probe.Provider),
                Detail = probe.Detail ?? string.Empty,
                IsActive = isActive,
                IsConnected = probe.Connected,
                IsRetrying = probe.Provider.Backend == _retryingBackend,
                Support = _support.GetValueOrDefault(probe.Provider.Backend),

                // A row that cannot be switched to is a row that can be asked again — never a dead
                // button (KON-117, KON-328). Clicking an unreachable backend is the most direct way a
                // user can say "it is running now", and before this it did nothing at all.
                SwitchCommand = isActive ? null : probe.Connected ? SwitchEngineCommand : RetryBackendCommand,
            };

            (probe.Provider.Kind == BackendKind.Cluster ? Clusters : Engines).Add(option);
        }

        OnPropertyChanged(nameof(HasClusters));
    }

    /// <summary>
    /// Whether a probed backend is worth a row in the switcher (KON-255). Everything is, except a
    /// built-in engine this machine shows no sign of having: the catalog offers Docker and Podman
    /// whether or not they are installed, so on a Docker-only machine Podman sat there permanently as
    /// an unclickable "Not connected" row — noise next to the Clusters group, which leaves itself out
    /// when there is no kubeconfig.
    /// <para>
    /// Four things keep a row that <see cref="IBackendProvider.IsInstalled"/> says no about:
    /// </para>
    /// <list type="bullet">
    /// <item><description>It answered. Whatever the provider thinks, something is there.</description></item>
    /// <item><description>It is the active backend — the shell is connected to it right now.</description></item>
    /// <item><description>It is what startup would open (pinned, or last used): <c>ConnectPreferredAsync</c>
    /// says "… is gone" about that backend, and the row is where the user goes to look.</description></item>
    /// </list>
    /// <para>
    /// Remotes, kube-contexts and anything a plugin contributes never reach the question:
    /// <see cref="IBackendProvider.IsInstalled"/> defaults to true, and they are in the list because
    /// someone added them. Nothing here needs to name them separately.
    /// </para>
    /// <para>
    /// Settings › Engines is deliberately not filtered: there, "Podman is not installed here" is the
    /// answer to a question the page exists to ask. This is the switcher only.
    /// </para>
    /// </summary>
    private bool BelongsInSwitcher(BackendProbe probe, bool isActive) =>
        probe.Provider.IsInstalled
        || probe.Connected
        || isActive
        || probe.Provider.Backend == _settings.ResolvedPinnedBackend
        || probe.Provider.Backend == _settings.LastBackend;
}
