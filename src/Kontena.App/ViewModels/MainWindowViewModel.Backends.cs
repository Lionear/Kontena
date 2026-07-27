using CommunityToolkit.Mvvm.Input;
using Kontena.App.Services;
using Kontena.Core;
using Kontena.Core.Errors;
using Kontena.Core.Models;
using Kontena.Core.Orchestration;
using Kontena.Engines;

namespace Kontena.App.ViewModels;

/// <summary>
/// Connecting to a backend and moving between them: first launch, onboarding, probing, the
/// switcher's list, and the engine-down state when none of it worked.
/// </summary>
public partial class MainWindowViewModel
{
    private async Task InitAsync()
    {
        try
        {
            _probes = await _registry.ProbeAllAsync();
            BuildSettingsPage();
            RebuildEngineList();
            RefreshNewClusters();

            if (!_settings.Onboarded)
            {
                EnterOnboarding();
                return;
            }

            await ConnectPreferredAsync();

            // After the shell is usable, never before: a slow or unreachable update server must not
            // hold up connecting to an engine, which is what the user actually opened Kontena for.
            _ = Update.CheckAsync();
        }
        catch (Exception ex)
        {
            EnterBackendDown("Can't reach a container engine", ex.Message);
        }
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
                Unreachable(wanted));
            return;
        }

        var real = _probes.FirstOrDefault(p =>
            p.Connected && p.Provider.Kind == BackendKind.Engine && p.Provider.Backend != FakeBackend);

        if (real is null)
        {
            EnterBackendDown(
                "Can't reach a container engine",
                "No Docker or Podman socket answered. The engine may be stopped, still starting, or you may not have permission to access it.");
            return;
        }

        await ActivateAsync(real.Provider);
    }
    /// <summary>Why a known backend did not answer, in terms that fit what it is.</summary>
    private string Unreachable(BackendProbe probe) => probe.Provider.Kind == BackendKind.Cluster
        ? $"The apiserver for {NameOf(probe.Provider)} did not answer. The cluster may be stopped, unreachable from this network, or your credentials may have expired."
        : $"The {NameOf(probe.Provider)} socket did not answer. It may be stopped, still starting, or you may not have permission to access it.";
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
    private void EnterOnboarding()
    {
        IsReady = false;
        IsBackendDown = false;
        CurrentPage = null;
        Onboarding = new OnboardingViewModel(
            _probes.Where(p => p.Provider.Kind == BackendKind.Engine).ToList(),
            FakeBackend,
            _settings.AutoDetectEngines,
            onContinue: backend => _ = CompleteOnboardingAsync(backend),
            onSkip: () => _ = CompleteOnboardingAsync(null),
            onInstallPodman: () => Browser.OpenUrl("https://podman.io/docs/installation"),
            nameOf: NameOf);
        IsOnboarding = true;
    }
    private async Task CompleteOnboardingAsync(string? backend)
    {
        var autoDetect = Onboarding?.AutoDetect ?? _settings.AutoDetectEngines;

        // Onboarding no longer pins: picking an engine here says "start me here", not "and never
        // follow me anywhere else". Activating it records it as last used, which is enough.
        _settings = _store.Update(s => s with
        {
            Onboarded = true,
            AutoDetectEngines = autoDetect,
        });
        BuildSettingsPage(); // reflect the just-chosen default in Settings

        IsOnboarding = false;
        Onboarding = null;

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
    private void EnterBackendDown(string title, string detail)
    {
        IsReady = false;
        IsBackendDown = true;
        BackendDownTitle = title;
        BackendDownDetail = detail;
        IsClusterMode = false;
        EngineName = "Not connected";
        EngineChip = "!";
        EngineDetail = "not connected";
        EngineEndpoint = string.Empty;
        CurrentPage = null;
        OnPropertyChanged(nameof(HasAlternatives));
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
        (_engine as IDisposable)?.Dispose();
        (_cluster as IDisposable)?.Dispose();
        _engine = null;
        _cluster = null;

        IsReady = false;
        IsBackendDown = false;

        var backend = provider.CreateBackend();
        _activeBackend = provider.Backend;
        EngineName = NameOf(provider);
        EngineChip = provider.Chip;

        RebuildEngineList();
        DisposeDetail();
        CloseDialog();

        if (backend is IClusterEngine cluster)
        {
            if (!await EnterClusterModeAsync(cluster))
                return;
        }
        else if (backend is IContainerEngine engine)
        {
            await EnterEngineModeAsync(engine);
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
    private async Task EnterEngineModeAsync(IContainerEngine engine)
    {
        _engine = engine;
        IsClusterMode = false;
        SetEngineNav();

        Containers = new ContainersViewModel(_engine)
        {
            RequestOpenDetail = ShowContainerDetail,
            RequestRunContainer = image => _ = ShowRunDialogAsync(image),
            RequestPullImage = ShowPullDialog,
            RequestConfirm = ShowConfirm,
        };
        Images = new ImagesViewModel(_engine)
        {
            RequestPullImage = ShowPullDialog,
            RequestBuildImage = ShowBuildDialog,
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
        Activity = new ActivityViewModel(_activityLog);

        SearchText = string.Empty;
        CurrentPage = Containers;

        await Containers.LoadAsync();
        IsReady = true;
        Containers.StartWatching();
        _activityLog.Attach(_engine, _activeBackend, ResolveEventName);

        await UpdateNavCountsAsync();
    }
    /// <summary>Returns false when the cluster could not be opened and the down state took over.</summary>
    private async Task<bool> EnterClusterModeAsync(IClusterEngine cluster)
    {
        _cluster = cluster;
        IsClusterMode = true;
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

        Namespaces.Clear();
        Namespaces.Add(AllNamespaces);
        foreach (var ns in await cluster.ListNamespacesAsync())
            Namespaces.Add(ns.Name);

        // Only now that the cluster answered: offering to reopen tunnels on a cluster we cannot reach
        // would be an empty promise (KON-105).
        RestorePortForwards(cluster, _activeBackend);

        SearchText = string.Empty;
        CurrentPage = new ClusterOverviewViewModel(cluster);
        SelectedNamespace = AllNamespaces; // OnSelectedNamespaceChanged refreshes the nav counts
        await UpdateClusterNavCountsAsync();
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

        return Containers?.Items.FirstOrDefault(c =>
            c.Id == ev.ResourceId
            || c.Id.StartsWith(ev.ResourceId, StringComparison.Ordinal)
            || ev.ResourceId.StartsWith(c.Id, StringComparison.Ordinal))?.Name;
    }
    private void BuildSettingsPage()
    {
        var all = _probes.Select(p => new EngineListItem(
            p.Provider.Backend, NameOf(p.Provider), p.Provider.Chip,
            p.Detail ?? string.Empty, p.Connected,
            p.Provider.Backend == _settings.ResolvedPinnedBackend,
            p.Provider.DisplayName)).ToList();

        // The detected-engines list stays engine-only; what you can pin does not — a cluster is a
        // perfectly reasonable thing to always start on.
        var engines = all
            .Where(e => _probes.First(p => p.Provider.Backend == e.Backend).Provider.Kind == BackendKind.Engine)
            .ToList();

        SettingsPage = new SettingsViewModel(
            _store, _settings, engines, all, ReloadBackendsAsync, Update,
            secrets: _secrets, registries: _registryCredentials, engine: () => _engine,
            // Adding or removing a remote changes the provider list, which is what the switcher is built
            // from — so the same rebuild the demo toggle uses (KON-46).
            onRemotesChanged: () => ReloadBackendsAsync(BackendCatalog.ShouldIncludeDemo(_settings.ShowDemoBackends)),
            // A rename changes no connection, so it must not cost a re-probe: re-read the names and
            // redraw. Probing on every keystroke would make typing a name feel like a reconnect.
            onNamesChanged: RefreshBackendNames,

            // Every cluster in every kubeconfig, not only the chosen ones — the hidden ones are exactly
            // what this list is for (KON-120).
            clusters: DiscoveredClusters(),
            onClustersChanged: () =>
                ReloadBackendsAsync(BackendCatalog.ShouldIncludeDemo(_settings.ShowDemoBackends)),
            kubeconfigs: Kubeconfigs())
        {
            // Local clusters (KON-109). Built here so it lives as long as the settings page and gets
            // disposed with it — an install left running against a page nobody is on would finish out
            // of sight.
            ClusterTooling = new ClusterToolingViewModel
            {
                RequestOpenUrl = Browser.OpenUrl,
                RequestConfirm = ShowConfirm,
            },
        };

        SettingsPage.RequestConfirm = ShowConfirm;
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

        _registry.Replace(BackendCatalog.Build(
            BackendCatalog.ShouldIncludeDemo(includeDemo),
            stored.RemoteEngines, stored.KubeconfigPaths, stored.ShowsCluster));
        _probes = await _registry.ProbeAllAsync();
        RefreshNewClusters();

        RebuildEngineList();
        BuildSettingsPage();

        if (_registry.Providers.Any(p => p.Backend == _activeBackend))
            return;

        var replacement = _probes.FirstOrDefault(p => p.Connected && p.Provider.Kind == BackendKind.Engine)
                          ?? _probes.FirstOrDefault(p => p.Connected);
        if (replacement is not null)
            await ActivateAsync(replacement.Provider);
        else
            EnterBackendDown("No backend is reachable", "Nothing answered after the backend list changed. Start an engine, or turn the demo backends back on in Settings.");
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
    private void RebuildEngineList()
    {
        Engines.Clear();
        Clusters.Clear();
        foreach (var probe in _probes)
        {
            var isActive = probe.Provider.Backend == _activeBackend;
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
                Chip = probe.Provider.Chip,
                Detail = probe.Detail ?? string.Empty,
                IsActive = isActive,
                IsConnected = probe.Connected,
                SwitchCommand = probe.Connected && !isActive ? SwitchEngineCommand : null,
            };

            (probe.Provider.Kind == BackendKind.Cluster ? Clusters : Engines).Add(option);
        }

        OnPropertyChanged(nameof(HasClusters));
    }
}
