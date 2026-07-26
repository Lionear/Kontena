using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.App.Services;
using Kontena.Core;
using Kontena.Core.Errors;
using Kontena.Core.Models;
using Kontena.Core.Orchestration;
using Kontena.Core.Orchestration.Models;
using Kontena.Engines;
using Kontena.Engines.Fakes;

namespace Kontena.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly BackendRegistry _registry;
    private readonly SettingsStore _store;
    private KontenaSettings _settings;
    private IReadOnlyList<BackendProbe> _probes = [];
    private IContainerEngine? _engine;
    private IClusterEngine? _cluster;
    private string _activeBackend = string.Empty;
    private ContainerDetailViewModel? _detail;
    private ClusterPodDetailViewModel? _podDetail;
    private readonly ActivityLog _activityLog = new();

    // Port forwards outlive the modal that starts them and belong to the cluster connection, so the
    // registry lives here — see PortForwardRegistry.
    private readonly PortForwardRegistry _portForwards = new();

    /// <summary>The live tunnels, for anything driving the shell from outside (the screenshot harness).</summary>
    public PortForwardRegistry PortForwards => _portForwards;

    /// <summary>Set while a connection is being torn down — see <see cref="RememberPortForwards"/>.</summary>
    private bool _suspendPortForwardMemory;

    /// <summary>Design-time / default ctor uses a fake-only registry.</summary>
    public MainWindowViewModel()
        : this(new BackendRegistry([new FakeEngineProvider()]))
    {
    }

    public MainWindowViewModel(BackendRegistry registry)
        : this(registry, new SettingsStore(), new KontenaSettings())
    {
    }

    /// <param name="updateService">The updater. Defaults to the real one; the screenshot harness
    /// passes a fake, because the card's interesting states need a packaged install that is behind
    /// — which a development run never is.</param>
    public MainWindowViewModel(
        BackendRegistry registry, SettingsStore store, KontenaSettings settings,
        IUpdateService? updateService = null)
    {
        _registry = registry;
        _store = store;
        _settings = settings;
        _updateService = updateService ?? new VelopackUpdateService();

        NavItems = [];
        SetEngineNav();
        _portForwards.Changed += OnPortForwardsChanged;

        // The card lives in the same modal slot as every other dialog, so an update never competes
        // with a Run or a Confirm for the screen.
        // Read through the store, not this class's copy: the Settings page saves its own record, so
        // the field here still says what it said at launch. Reading fresh is what makes a channel
        // switch or an auto-download toggle take effect on the next check instead of after a restart.
        Update = new UpdateViewModel(
            _updateService, store, store.Load,
            openCard: () => Dialog = Update,
            closeCard: () => { if (ReferenceEquals(Dialog, Update)) Dialog = null; });

        // One resolver for the process: it reads the keychain and the engine's config on demand, so it
        // holds no secret of its own.
        _registryCredentials = new RegistryCredentials(_secrets, store.Load);

        SyncThemeToggleIcon();
        _ = InitAsync();
    }

    private readonly IUpdateService _updateService;
    private readonly ISecretStore _secrets = SecretStore.Create();
    private readonly RegistryCredentials _registryCredentials;

    /// <summary>The in-app updater, behind the sidebar entry, the toast and the card (KON-110).</summary>
    public UpdateViewModel Update { get; }

    // Pages
    [ObservableProperty] private ContainersViewModel? _containers;
    [ObservableProperty] private ImagesViewModel? _images;
    [ObservableProperty] private VolumesViewModel? _volumes;
    [ObservableProperty] private NetworksViewModel? _networks;
    [ObservableProperty] private ComposeProjectsViewModel? _composeProjects;
    [ObservableProperty] private SettingsViewModel? _settingsPage;
    [ObservableProperty] private ActivityViewModel? _activity;

    /// <summary>The page shown in the content area.</summary>
    [ObservableProperty] private object? _currentPage;

    public bool IsActivitySelected => Activity is not null && ReferenceEquals(CurrentPage, Activity);
    public bool IsSettingsSelected => SettingsPage is not null && ReferenceEquals(CurrentPage, SettingsPage);

    partial void OnCurrentPageChanged(object? value)
    {
        OnPropertyChanged(nameof(IsActivitySelected));
        OnPropertyChanged(nameof(IsSettingsSelected));
    }

    /// <summary>The active modal dialog (e.g. Run container), or null when none.</summary>
    [ObservableProperty] private object? _dialog;

    public bool IsDialogOpen => Dialog is not null;

    partial void OnDialogChanged(object? value) => OnPropertyChanged(nameof(IsDialogOpen));

    public ObservableCollection<NavItem> NavItems { get; }

    /// <summary>Container engines shown in the switcher's "Container engines" group.</summary>
    public ObservableCollection<EngineOption> Engines { get; } = [];

    /// <summary>Clusters shown in the switcher's "Clusters · Orchestrators" group.</summary>
    public ObservableCollection<EngineOption> Clusters { get; } = [];

    /// <summary>Whether any clusters are known (drives the popover's Clusters section).</summary>
    public bool HasClusters => Clusters.Count > 0;

    /// <summary>True when a cluster (OAL) is active — swaps the nav and shows the namespace picker.</summary>
    [ObservableProperty] private bool _isClusterMode;

    /// <summary>Namespaces for the cluster-mode picker; the first entry is "All namespaces".</summary>
    public ObservableCollection<string> Namespaces { get; } = [];

    /// <summary>The selected namespace filter in cluster mode.</summary>
    [ObservableProperty] private string? _selectedNamespace;

    private const string AllNamespaces = "All namespaces";

    [ObservableProperty] private string _engineName = "Connecting…";
    [ObservableProperty] private string _engineChip = "?";

    /// <summary>Second line of the sidebar pill — the active backend's version/kind.</summary>
    [ObservableProperty] private string _engineDetail = string.Empty;

    /// <summary>Third line of the sidebar pill — the active backend's endpoint (socket/URL).</summary>
    [ObservableProperty] private string _engineEndpoint = string.Empty;

    /// <summary>False until the first page is on screen (drives the connecting state).</summary>
    [ObservableProperty] private bool _isReady;

    /// <summary>True when the backend Kontena tried to open is not usable — shows the down state.</summary>
    [ObservableProperty] private bool _isBackendDown;

    /// <summary>Headline of the down state; names what could not be opened.</summary>
    [ObservableProperty] private string _backendDownTitle = "Can't reach a container engine";

    [ObservableProperty] private string _backendDownDetail = string.Empty;

    /// <summary>Whether there is anything else to switch to from the down state.</summary>
    public bool HasAlternatives => Engines.Count > 0 || Clusters.Count > 0;

    /// <summary>True on first run — shows the full-window onboarding (engine connect) wizard.</summary>
    [ObservableProperty] private bool _isOnboarding;

    /// <summary>The first-run wizard view model, or null when not onboarding.</summary>
    [ObservableProperty] private OnboardingViewModel? _onboarding;

    /// <summary>The connecting state shows only while neither ready, down, nor onboarding.</summary>
    public bool IsConnecting => !IsReady && !IsBackendDown && !IsOnboarding;

    partial void OnIsReadyChanged(bool value) => OnPropertyChanged(nameof(IsConnecting));
    partial void OnIsBackendDownChanged(bool value) => OnPropertyChanged(nameof(IsConnecting));
    partial void OnIsOnboardingChanged(bool value) => OnPropertyChanged(nameof(IsConnecting));

    private const string FakeBackend = "fake";

    /// <summary>Shared command-bar search; forwarded to the active page.</summary>
    [ObservableProperty] private string _searchText = string.Empty;

    partial void OnSearchTextChanged(string value)
    {
        if (CurrentPage is IListPage page)
            page.SearchText = value;
    }

    [RelayCommand]
    private void Navigate(string key)
    {
        if (IsClusterMode)
        {
            NavigateCluster(key);
            return;
        }

        IListPage? page = key switch
        {
            "images" => Images,
            "volumes" => Volumes,
            "networks" => Networks,
            "projects" => ComposeProjects,
            "containers" => Containers,
            _ => Containers,
        };
        if (page is null)
            return;

        DisposeDetail();
        CurrentPage = page;
        foreach (var item in NavItems)
            item.IsSelected = item.Key == key;

        SearchText = page.SearchText;

        if (!page.HasLoaded)
            _ = page.LoadAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            _probes = await _registry.ProbeAllAsync();
            BuildSettingsPage();
            RebuildEngineList();

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
        };
        Images = new ImagesViewModel(_engine)
        {
            RequestPullImage = ShowPullDialog,
            RequestBuildImage = ShowBuildDialog,
        };
        Volumes = new VolumesViewModel(_engine)
        {
            RequestCreateVolume = ShowCreateVolumeDialog,
            RequestBrowseVolume = ShowBrowseVolumeDialog,
        };
        Networks = new NetworksViewModel(_engine)
        {
            RequestCreateNetwork = ShowCreateNetworkDialog,
            RequestNetworkAttachments = ShowNetworkAttachmentsDialog,
        };
        ComposeProjects = new ComposeProjectsViewModel(_engine)
        {
            RequestOpenDetail = ShowContainerDetail,
            RequestNewProject = ShowComposeUpDialog,
            RequestProjectLogs = ShowComposeLogsDialog,
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

    /// <summary>The engine (CEAL) sidebar nav — Containers/Images/Volumes/Networks/Projects.</summary>
    private void SetEngineNav()
    {
        NavItems.Clear();
        NavItems.Add(new NavItem("containers", "Containers", "IconContainer") { IsSelected = true });
        NavItems.Add(new NavItem("images", "Images", "IconLayers"));
        NavItems.Add(new NavItem("volumes", "Volumes", "IconDatabase"));
        NavItems.Add(new NavItem("networks", "Networks", "IconNetwork"));
        NavItems.Add(new NavItem("projects", "Projects", "IconBox"));
        foreach (var item in NavItems)
            item.Command = NavigateCommand;
    }

    /// <summary>The cluster (OAL) sidebar nav — the Kubernetes resource tree.</summary>
    private void SetClusterNav()
    {
        NavItems.Clear();
        NavItems.Add(new NavItem("overview", "Overview", "IconGauge") { IsSelected = true });
        NavItems.Add(new NavItem("nodes", "Nodes", "IconCpu"));
        NavItems.Add(new NavItem("namespaces", "Namespaces", "IconBox"));
        NavItems.Add(new NavItem("workloads", "Workloads", "IconLayers"));
        NavItems.Add(new NavItem("pods", "Pods", "IconContainer"));
        NavItems.Add(new NavItem("services", "Services", "IconNetwork"));
        NavItems.Add(new NavItem("portforwards", "Port forwards", "IconPlug"));
        NavItems.Add(new NavItem("apply", "Apply manifest", "IconPlay"));
        foreach (var item in NavItems)
            item.Command = NavigateCommand;
    }

    private void NavigateCluster(string key)
    {
        if (_cluster is null)
            return;

        DisposeDetail();
        (CurrentPage as PortForwardsViewModel)?.Dispose();
        foreach (var item in NavItems)
            item.IsSelected = item.Key == key;

        // Nodes/Namespaces are cluster-wide; the rest honour the namespace picker.
        CurrentPage = key switch
        {
            "overview" => new ClusterOverviewViewModel(_cluster),
            "nodes" => new ClusterNodesViewModel(_cluster),
            "namespaces" => new ClusterNamespacesViewModel(_cluster),
            "workloads" => new ClusterWorkloadsViewModel(_cluster, ActiveNamespace, ShowScaleDialog, ConfirmRestartWorkload),
            "pods" => new ClusterPodsViewModel(_cluster, ActiveNamespace, ShowPodDetail, ConfirmDeletePod),
            "services" => new ClusterServicesViewModel(_cluster, ActiveNamespace, ShowServicePortForward),
            "portforwards" => new PortForwardsViewModel(_portForwards),
            "apply" => new ApplyManifestViewModel(_cluster, EngineName, onApplied: () =>
            {
                // An apply can create or remove anything — refresh the counts, not the open page.
                _ = UpdateClusterNavCountsAsync();
                return Task.CompletedTask;
            }, ActiveNamespace),
            _ => new ClusterOverviewViewModel(_cluster),
        };
        SearchText = string.Empty;
    }

    /// <summary>The namespace filter, or null when "All namespaces" is selected.</summary>
    private string? ActiveNamespace => SelectedNamespace is null or AllNamespaces ? null : SelectedNamespace;

    /// <summary>Rebuild the currently-selected cluster page (e.g. after an action mutates it).</summary>
    private void ReloadCurrentClusterPage()
    {
        if (!IsClusterMode)
            return;

        var key = NavItems.FirstOrDefault(i => i.IsSelected)?.Key ?? "overview";
        NavigateCluster(key);
        _ = UpdateClusterNavCountsAsync();
    }

    // ── Workload actions (KON-71) ───────────────────────────────────────────

    private void ShowScaleDialog(Workload workload)
    {
        if (_cluster is null)
            return;

        Dialog = new ScaleWorkloadViewModel(_cluster, workload, CloseDialog, onDone: () =>
        {
            CloseDialog();
            ReloadCurrentClusterPage();
            return Task.CompletedTask;
        });
    }

    private void ConfirmRestartWorkload(Workload workload)
    {
        if (_cluster is null)
            return;

        Dialog = new ConfirmViewModel(
            "Restart rollout",
            $"Roll out a restart of {workload.Kind} \"{workload.Name}\" in {workload.Namespace}? Its pods are recreated" +
            " a few at a time so the workload stays available.",
            "Restart",
            onConfirm: async () =>
            {
                var reference = new ResourceRef(new GroupVersionKind("apps", "v1", workload.Kind.ToString()), workload.Namespace, workload.Name);
                await _cluster.RolloutRestartAsync(reference);
                CloseDialog();
                ReloadCurrentClusterPage();
            },
            onClose: CloseDialog);
    }

    /// <summary>Delete a pod (KON-69) — destructive, so it always goes through a confirm.</summary>
    private void ConfirmDeletePod(Pod pod)
    {
        if (_cluster is null)
            return;

        Dialog = new ConfirmViewModel(
            "Delete pod",
            $"Delete pod \"{pod.Name}\" in {pod.Namespace}? If a controller owns it, a replacement is" +
            " scheduled straight away; if not, it is gone for good.",
            "Delete",
            onConfirm: async () =>
            {
                await _cluster.DeleteAsync(new ResourceRef(GroupVersionKind.Pod, pod.Namespace, pod.Name));
                CloseDialog();
                ReloadCurrentClusterPage();
            },
            onClose: CloseDialog,
            destructive: true);
    }

    private void ShowServicePortForward(Service service)
    {
        if (_cluster is null)
            return;

        var reference = new ResourceRef(GroupVersionKind.Service, service.Namespace, service.Name);
        var ports = service.Ports.Select(p => p.Port).ToList();
        Dialog = new PortForwardViewModel(
            _portForwards, _cluster, reference, $"{service.Name} · {service.Namespace}", ports, CloseDialog,
            UpdatePortForwardCount);
    }

    private void ShowPodPortForward(Pod pod)
    {
        if (_cluster is null)
            return;

        var reference = new ResourceRef(GroupVersionKind.Pod, pod.Namespace, pod.Name);
        Dialog = new PortForwardViewModel(
            _portForwards, _cluster, reference, $"{pod.Name} · {pod.Namespace}", [], CloseDialog,
            UpdatePortForwardCount);
    }

    private async Task UpdateClusterNavCountsAsync()
    {
        if (_cluster is null)
            return;

        var ci = CultureInfo.InvariantCulture;
        var ns = SelectedNamespace == AllNamespaces ? null : SelectedNamespace;
        SetNavCount("nodes", (await _cluster.ListNodesAsync()).Count.ToString(ci));
        SetNavCount("namespaces", (await _cluster.ListNamespacesAsync()).Count.ToString(ci));
        SetNavCount("workloads", (await _cluster.ListWorkloadsAsync(null, ns)).Count.ToString(ci));
        SetNavCount("pods", (await _cluster.ListPodsAsync(ns)).Count.ToString(ci));
        SetNavCount("services", (await _cluster.ListServicesAsync(ns)).Count.ToString(ci));
        UpdatePortForwardCount();
    }

    // Keyed rather than indexed: the nav gained an entry in the middle once already, and an index-based
    // assignment puts the pod count on Services the day it gains another.
    private void SetNavCount(string key, string count)
    {
        if (NavItems.FirstOrDefault(i => i.Key == key) is { } item)
            item.Count = count;
    }

    /// <summary>
    /// Badge the sidebar with the number of live tunnels — the whole point of the page is that they keep
    /// running while you are somewhere else — plus a marker when one fell over (KON-107).
    /// <para>
    /// The count stays "how many are working", which is why the last tunnel dropping takes the badge to
    /// nothing. That is correct and also unhelpful on its own: the page suddenly has something worth
    /// seeing and the nav says it is empty. So a dropped tunnel gets its own marker, and only a dropped
    /// one — paused and remembered rows are states the user chose, not events.
    /// </para>
    /// </summary>
    private void UpdatePortForwardCount()
    {
        SetNavCount("portforwards", _portForwards.ActiveCount == 0
            ? string.Empty
            : _portForwards.ActiveCount.ToString(CultureInfo.InvariantCulture));

        if (NavItems.FirstOrDefault(i => i.Key == "portforwards") is not { } item)
            return;

        var dropped = _portForwards.DroppedCount;
        item.NeedsAttention = dropped > 0;
        item.AttentionTip = dropped switch
        {
            0 => string.Empty,
            1 => "A port forward dropped",
            _ => $"{dropped} port forwards dropped",
        };
    }

    private void OnPortForwardsChanged()
    {
        UpdatePortForwardCount();
        RememberPortForwards();
    }

    /// <summary>
    /// Keep the current list for the backend it belongs to, so the next visit can offer it back
    /// (KON-105). Suspended while tearing a connection down: that clears the registry, and writing
    /// then would erase exactly the list we mean to keep.
    /// </summary>
    private void RememberPortForwards()
    {
        if (_suspendPortForwardMemory || string.IsNullOrEmpty(_activeBackend))
            return;

        var remembered = _portForwards.Snapshot();
        _settings = _store.Update(s =>
        {
            var all = new Dictionary<string, IReadOnlyList<RememberedPortForward>>(s.PortForwards);
            if (remembered.Count == 0)
                all.Remove(_activeBackend);
            else
                all[_activeBackend] = remembered;

            return s with { PortForwards = all };
        });
    }

    /// <summary>
    /// Tear the tunnels down without forgetting them: the list is written first, then cleared with
    /// persistence suspended, so leaving a cluster keeps what you had open on it.
    /// </summary>
    private async Task StopPortForwardsAsync()
    {
        RememberPortForwards();
        _suspendPortForwardMemory = true;
        try
        {
            await _portForwards.StopAllAsync();
        }
        finally
        {
            _suspendPortForwardMemory = false;
        }
    }

    /// <summary>Put back what this cluster had open last time, closed and waiting for a click.</summary>
    private void RestorePortForwards(IClusterEngine cluster, string backend)
    {
        if (_settings.PortForwards.TryGetValue(backend, out var remembered) && remembered.Count > 0)
            _portForwards.Restore(cluster, remembered);
    }

    partial void OnSelectedNamespaceChanged(string? value)
    {
        if (!IsClusterMode)
            return;

        // Reload the visible namespaced grid and refresh the nav counts.
        var key = NavItems.FirstOrDefault(i => i.IsSelected)?.Key ?? "overview";
        NavigateCluster(key);
        _ = UpdateClusterNavCountsAsync();
    }

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

    [RelayCommand]
    private void ShowActivity()
    {
        if (Activity is null)
            return;

        DisposeDetail();
        CurrentPage = Activity;
        SearchText = Activity.SearchText;
        foreach (var item in NavItems)
            item.IsSelected = false;
    }

    [RelayCommand]
    private async Task RefreshCurrentPageAsync()
    {
        if (CurrentPage is IListPage page)
            await page.LoadAsync();
    }

    private void ShowContainerDetail(ContainerSummary summary)
    {
        if (_engine is null)
            return;

        DisposeDetail();

        // Reload settings so a just-changed terminal font is picked up.
        var current = _store.Load();
        var font = new TerminalFont(current.TerminalFontFamily, current.TerminalFontSize, current.TerminalLigatures);

        _detail = new ContainerDetailViewModel(_engine, summary, ShowContainers, font);
        CurrentPage = _detail;
    }

    private void ShowContainers()
    {
        DisposeDetail();
        if (Containers is null)
            return;

        CurrentPage = Containers;
        SearchText = Containers.SearchText;
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
            onNamesChanged: RefreshBackendNames);
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
        _registry.Replace(BackendCatalog.Build(
            BackendCatalog.ShouldIncludeDemo(includeDemo), stored.RemoteEngines, stored.KubeconfigPaths));
        _probes = await _registry.ProbeAllAsync();

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

    private void ShowNetworkAttachmentsDialog(NetworkSummary network)
    {
        if (_engine is null)
            return;

        Dialog = new NetworkAttachmentsViewModel(_engine, network, CloseDialog, onChanged: async () =>
        {
            if (Networks is not null)
                await Networks.LoadAsync();
        });
    }

    private void ShowCreateNetworkDialog()
    {
        if (_engine is null)
            return;

        Dialog = new CreateNetworkViewModel(_engine, CloseDialog, onCreated: async () =>
        {
            if (Networks is not null)
                await Networks.LoadAsync();
            await UpdateNavCountsAsync();
        });
    }

    [RelayCommand]
    private void ShowBrowseVolumeDialog(string volume)
    {
        if (_engine is null)
            return;

        Dialog = new BrowseVolumeViewModel(_engine, volume, CloseDialog);
    }

    private void ShowCreateVolumeDialog()
    {
        if (_engine is null)
            return;

        Dialog = new CreateVolumeViewModel(_engine, CloseDialog, onCreated: async () =>
        {
            if (Volumes is not null)
                await Volumes.LoadAsync();
            await UpdateNavCountsAsync();
        });
    }

    [RelayCommand]
    private void ShowSettings()
    {
        DisposeDetail();
        CloseDialog();
        if (SettingsPage is null)
            return;

        CurrentPage = SettingsPage;
        SearchText = string.Empty;
        foreach (var item in NavItems)
            item.IsSelected = false;
    }

    /// <summary>
    /// The switcher's "Add engine or cluster…" row (KON-118). Opens the wizard, which ends in a
    /// connection that has actually been made — the reason it is a wizard and not a form.
    /// </summary>
    [RelayCommand]
    private void ShowAddBackend()
    {
        Dialog = new AddBackendViewModel(_store, _probes, CloseDialog, async backend =>
        {
            await ReloadBackendsAsync(BackendCatalog.ShouldIncludeDemo(_settings.ShowDemoBackends));

            // Switch to what was just added, but only if it is really there: a rebuild can drop a
            // provider whose configuration turned out to be unusable.
            if (backend is { Length: > 0 }
                && _registry.Providers.FirstOrDefault(p => p.Backend == backend) is { } provider)
            {
                await ActivateAsync(provider);
            }
        });
    }

    // ── Theme quick-toggle (topbar) ─────────────────────────────────────────

    /// <summary>Resource key of the icon shown on the topbar theme button.</summary>
    [ObservableProperty] private string _themeToggleIconKey = "IconMoon";

    /// <summary>Tooltip for the topbar theme button.</summary>
    [ObservableProperty] private string _themeToggleTip = "Toggle theme";

    /// <summary>Flips between Light and Dark based on what is actually on screen.
    /// The three-way preference (incl. System) still lives in Settings.</summary>
    [RelayCommand]
    private void ToggleTheme()
    {
        var next = Application.Current?.ActualThemeVariant == ThemeVariant.Dark
            ? ThemePreference.Light
            : ThemePreference.Dark;

        if (SettingsPage is not null)
        {
            _settings = _settings with { Theme = next };
            SettingsPage.Theme = next; // applies + persists via its own handler
        }
        else
        {
            ThemeApplier.Apply(next);
            _settings = _store.Update(s => s with { Theme = next });
        }

        SyncThemeToggleIcon();
    }

    private void SyncThemeToggleIcon()
    {
        var isDark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
        ThemeToggleIconKey = isDark ? "IconSun" : "IconMoon";
        ThemeToggleTip = isDark ? "Switch to light theme" : "Switch to dark theme";
    }

    private void DisposeDetail()
    {
        _detail?.Dispose();
        _detail = null;
        _podDetail?.Dispose();
        _podDetail = null;
    }

    /// <summary>Open the pod-detail page for a pod (logs / shell / events / YAML).</summary>
    private void ShowPodDetail(Pod pod)
    {
        if (_cluster is null)
            return;

        DisposeDetail();

        var current = _store.Load();
        var font = new TerminalFont(current.TerminalFontFamily, current.TerminalFontSize, current.TerminalLigatures);

        _podDetail = new ClusterPodDetailViewModel(_cluster, pod, () => NavigateCluster("pods"), font, ShowPodPortForward);
        CurrentPage = _podDetail;
    }

    private async Task ShowRunDialogAsync(string? initialImage = null)
    {
        if (_engine is null)
            return;

        var networks = (await _engine.ListNetworksAsync()).Select(n => n.Name).ToList();
        var images = (await _engine.ListImagesAsync())
            .Select(i => $"{i.Repository}:{i.Tag}")
            .ToHashSet(StringComparer.Ordinal);

        Dialog = new RunContainerViewModel(
            _engine, EngineName, EngineChip, networks, images,
            onClose: CloseDialog,
            onCreated: async () =>
            {
                if (Containers is not null)
                    await Containers.LoadAsync();
            },
            initialImage: initialImage,
            credentials: _registryCredentials);
    }

    private void ShowPullDialog()
    {
        if (_engine is null)
            return;

        Dialog = new PullImageViewModel(
            _engine, CloseDialog, onPulled: RefreshAfterPullAsync, credentials: _registryCredentials);
    }

    private async Task RefreshAfterPullAsync()
    {
        if (Images is { HasLoaded: true })
            await Images.LoadAsync();
        await UpdateNavCountsAsync();
    }

    private void ShowBuildDialog()
    {
        if (_engine is null)
            return;

        Dialog = new BuildImageViewModel(_engine, CloseDialog,
            onRun: image =>
            {
                CloseDialog();
                _ = ShowRunDialogAsync(image);
            },
            recentContexts: _settings.RecentBuildContexts,
            onContextUsed: RecordRecentContext);
    }

    /// <summary>Remember a just-used build context, most-recent first, capped to a short list.</summary>
    private void RecordRecentContext(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        _settings = _store.Update(s =>
        {
            var recent = new List<string> { path };
            recent.AddRange(s.RecentBuildContexts
                .Where(c => !string.Equals(c, path, StringComparison.Ordinal)));

            return s with { RecentBuildContexts = recent.Take(6).ToList() };
        });
    }

    private void ShowComposeUpDialog()
    {
        if (_engine is null)
            return;

        Dialog = new ComposeUpViewModel(_engine, CloseDialog, onUp: RefreshComposeAsync);
    }

    private void ShowComposeLogsDialog(ComposeProjectViewModel project)
    {
        if (_engine is null)
            return;

        Dialog = new ComposeLogsViewModel(_engine, project.Name, project.LogSources, CloseDialog);
    }

    private async Task RefreshComposeAsync()
    {
        if (ComposeProjects is { HasLoaded: true })
            await ComposeProjects.LoadAsync();
        await UpdateNavCountsAsync();
    }

    private void CloseDialog()
    {
        (Dialog as IDisposable)?.Dispose();
        Dialog = null;
    }

    public void Dispose()
    {
        DisposeDetail();
        CloseDialog();
        StopPortForwardsAsync().GetAwaiter().GetResult();
        Containers?.Dispose();
        _activityLog.Dispose();
        (_engine as IDisposable)?.Dispose();
        (_cluster as IDisposable)?.Dispose();
        GC.SuppressFinalize(this);
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

    private async Task UpdateNavCountsAsync()
    {
        if (_engine is null || Containers is null)
            return;

        var ci = CultureInfo.InvariantCulture;
        NavItems[0].Count = Containers.Items.Count.ToString(ci);
        NavItems[1].Count = (await _engine.ListImagesAsync()).Count.ToString(ci);
        NavItems[2].Count = (await _engine.ListVolumesAsync()).Count.ToString(ci);
        NavItems[3].Count = (await _engine.ListNetworksAsync()).Count.ToString(ci);

        var projects = (await _engine.ListContainersAsync())
            .Where(c => c.Labels.ContainsKey(ComposeProjectsViewModel.ProjectLabel))
            .Select(c => c.Labels[ComposeProjectsViewModel.ProjectLabel])
            .Distinct()
            .Count();
        NavItems[4].Count = projects.ToString(ci);
    }
}
