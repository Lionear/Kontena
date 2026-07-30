using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Kontena.App.Services;
using Kontena.Sdk.Models;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk;
using Kontena.Engines.Fakes;
using Kontena.Core.Models;
using Kontena.Core.Orchestration;
using Kontena.Engines;

namespace Kontena.App.ViewModels;

/// <summary>
/// The shell: what backend is open, which page is showing, and what is in the modal slot.
/// <para>
/// This file holds that state and the lifetime around it. The behaviour lives in partials beside it
/// — <c>MainWindowViewModel.Backends.cs</c>, <c>.Navigation.cs</c>, <c>.Clusters.cs</c>,
/// <c>.Dialogs.cs</c>. Almost every feature reaches into this class somewhere, and as one file that
/// made it the place where unrelated branches met (KON-139).
/// </para>
/// </summary>
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
        // Rows carry a backend id, not a provider, so the logos the providers declare are remembered
        // here and again whenever the set changes (KON-80).
        BackendChips.Learn(registry.Providers);
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

        // Built here rather than with the other pages (KON-135, KON-137): these say nothing about a
        // backend, so they have to stay reachable when there is no working one to say it about.
        // Activity used to be rebuilt on every connect, which also meant every reconnect left the
        // previous one subscribed to the same log. One instance over the log's lifetime, which is
        // this class's lifetime.
        About = new AboutViewModel(_secrets, ShowActivity);
        Activity = new ActivityViewModel(_activityLog);

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

    /// <summary>
    /// The local-clusters page (KON-76). Held here rather than on the settings page because a create
    /// rebuilds that page — see <c>BuildLocalClustersPage</c>.
    /// </summary>
    private LocalClustersViewModel? _localClusters;
    [ObservableProperty] private ActivityViewModel? _activity;

    /// <summary>The About page (KON-135). Never null — it needs no backend to say what it says.</summary>
    public AboutViewModel About { get; }

    /// <summary>The page shown in the content area.</summary>
    [ObservableProperty] private object? _currentPage;

    public bool IsActivitySelected => Activity is not null && ReferenceEquals(CurrentPage, Activity);
    public bool IsSettingsSelected => SettingsPage is not null && ReferenceEquals(CurrentPage, SettingsPage);
    public bool IsAboutSelected => ReferenceEquals(CurrentPage, About);

    /// <summary>
    /// Whether the page on screen says nothing about a backend (KON-137).
    /// <para>
    /// These three are the ones you want most when nothing works: Settings is where the engine list,
    /// a remote or a kubeconfig gets fixed, Activity is where you see what happened just before it
    /// broke, and About has the version and the link you need to report it. So they show over the
    /// engine-down card rather than behind it.
    /// </para>
    /// </summary>
    public bool IsBackendIndependentPage => IsActivitySelected || IsSettingsSelected || IsAboutSelected;

    /// <summary>Whether the content area shows <see cref="CurrentPage"/> at all.</summary>
    public bool IsPageVisible => IsReady || IsBackendIndependentPage;

    /// <summary>Whether the engine-down card has the content area. It yields to the three pages above.</summary>
    public bool IsBackendDownVisible => IsBackendDown && !IsBackendIndependentPage;

    partial void OnCurrentPageChanged(object? value)
    {
        OnPropertyChanged(nameof(IsActivitySelected));
        OnPropertyChanged(nameof(IsSettingsSelected));
        OnPropertyChanged(nameof(IsAboutSelected));
        OnPropertyChanged(nameof(IsSearchEnabled));
        OnPropertyChanged(nameof(SearchPlaceholder));
        RefreshContentVisibility();
    }

    /// <summary>
    /// Whether the command-bar search does anything here. Off on pages that are not lists — Overview,
    /// Apply manifest, the Workloads dashboard — because a box that takes text and ignores it reads as
    /// "searched, found nothing" (KON-164).
    /// </summary>
    public bool IsSearchEnabled => CurrentPage is IListPage { SupportsSearch: true };

    /// <summary>The active page's own placeholder, or a neutral one where search is off.</summary>
    public string SearchPlaceholder => CurrentPage is IListPage { SupportsSearch: true } page
        ? page.SearchPlaceholder
        : "Search…";

    private void RefreshContentVisibility()
    {
        OnPropertyChanged(nameof(IsBackendIndependentPage));
        OnPropertyChanged(nameof(IsPageVisible));
        OnPropertyChanged(nameof(IsBackendDownVisible));
        OnPropertyChanged(nameof(IsConnecting));
    }

    /// <summary>The active modal dialog (e.g. Run container), or null when none.</summary>
    [ObservableProperty] private object? _dialog;

    public bool IsDialogOpen => Dialog is not null;

    partial void OnDialogChanged(object? value)
    {
        OnPropertyChanged(nameof(IsDialogOpen));

        // The Escape and Enter bindings hang off these (KON-201). Without the notification they keep
        // whatever they answered when the window was built, which is "no" — so a dialog would open and
        // then not answer either key.
        DismissCommand.NotifyCanExecuteChanged();
        ConfirmPrimaryCommand.NotifyCanExecuteChanged();
    }

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
    [ObservableProperty] private BackendChipInfo _engineChip = new("?");

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

    /// <summary>
    /// The connecting state shows only while neither ready, down, nor onboarding — and not over a
    /// page that does not need the connection it is waiting for (KON-137).
    /// </summary>
    public bool IsConnecting => !IsReady && !IsBackendDown && !IsOnboarding && !IsBackendIndependentPage;

    partial void OnIsReadyChanged(bool value) => RefreshContentVisibility();
    partial void OnIsBackendDownChanged(bool value) => RefreshContentVisibility();
    partial void OnIsOnboardingChanged(bool value) => RefreshContentVisibility();

    private const string FakeBackend = "fake";

    /// <summary>Shared command-bar search; forwarded to the active page.</summary>
    [ObservableProperty] private string _searchText = string.Empty;

    /// <summary>
    /// How long typing settles before the list is rebuilt. Long enough that a burst of keystrokes
    /// costs one rebuild instead of one per letter, short enough that it still feels like typing.
    /// </summary>
    internal TimeSpan SearchDebounce { get; set; } = TimeSpan.FromMilliseconds(150);

    private CancellationTokenSource? _searchDebounce;

    /// <summary>The pending push, so a test can wait for it rather than sleep and hope.</summary>
    internal Task SearchSettled { get; private set; } = Task.CompletedTask;

    partial void OnSearchTextChanged(string value)
    {
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
        _searchDebounce = null;

        if (CurrentPage is not IListPage { SupportsSearch: true } page)
            return;

        // Clearing is not typing. Waiting to show everything again is the one case where the delay is
        // pure lag: there is no next keystroke coming to make it worth collapsing.
        if (value.Length == 0 || SearchDebounce <= TimeSpan.Zero)
        {
            page.SearchText = value;
            SearchSettled = Task.CompletedTask;
            return;
        }

        var cts = new CancellationTokenSource();
        _searchDebounce = cts;
        SearchSettled = PushSearchAsync(page, value, cts.Token);
    }

    private async Task PushSearchAsync(IListPage target, string value, CancellationToken ct)
    {
        try
        {
            await Task.Delay(SearchDebounce, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // The page is captured, not looked up again. Navigating during the delay would otherwise land
        // the term on whatever page arrived next — filtering something the user never searched, on a
        // page whose box looks empty.
        if (!ct.IsCancellationRequested && ReferenceEquals(CurrentPage, target))
            target.SearchText = value;
    }
    private void DisposeDetail()
    {
        _detail?.Dispose();
        _detail = null;
        _podDetail?.Dispose();
        _podDetail = null;
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
}
