using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.App.Services;
using Kontena.Core.Models;
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
    private string _activeBackend = string.Empty;
    private ContainerDetailViewModel? _detail;
    private readonly ActivityLog _activityLog = new();

    /// <summary>Design-time / default ctor uses a fake-only registry.</summary>
    public MainWindowViewModel()
        : this(new BackendRegistry([new FakeEngineProvider()]))
    {
    }

    public MainWindowViewModel(BackendRegistry registry)
        : this(registry, new SettingsStore(), new KontenaSettings())
    {
    }

    public MainWindowViewModel(BackendRegistry registry, SettingsStore store, KontenaSettings settings)
    {
        _registry = registry;
        _store = store;
        _settings = settings;

        NavItems =
        [
            new NavItem("containers", "Containers", "IconContainer") { IsSelected = true },
            new NavItem("images", "Images", "IconLayers"),
            new NavItem("volumes", "Volumes", "IconDatabase"),
            new NavItem("networks", "Networks", "IconNetwork"),
            new NavItem("projects", "Projects", "IconBox"),
        ];
        foreach (var item in NavItems)
            item.Command = NavigateCommand;

        SyncThemeToggleIcon();
        _ = InitAsync();
    }

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

    /// <summary>Engines shown in the backend-switcher dropdown.</summary>
    public ObservableCollection<EngineOption> Engines { get; } = [];

    [ObservableProperty] private string _engineName = "Connecting…";
    [ObservableProperty] private string _engineChip = "?";

    /// <summary>Second line of the sidebar pill — the active backend's version/kind.</summary>
    [ObservableProperty] private string _engineDetail = string.Empty;

    /// <summary>Third line of the sidebar pill — the active backend's endpoint (socket/URL).</summary>
    [ObservableProperty] private string _engineEndpoint = string.Empty;

    /// <summary>False until the first page is on screen (drives the connecting state).</summary>
    [ObservableProperty] private bool _isReady;

    /// <summary>True when no container engine could be reached — shows the engine-down state.</summary>
    [ObservableProperty] private bool _isEngineDown;

    [ObservableProperty] private string _engineDownDetail = string.Empty;

    /// <summary>True on first run — shows the full-window onboarding (engine connect) wizard.</summary>
    [ObservableProperty] private bool _isOnboarding;

    /// <summary>The first-run wizard view model, or null when not onboarding.</summary>
    [ObservableProperty] private OnboardingViewModel? _onboarding;

    /// <summary>The connecting state shows only while neither ready, engine-down, nor onboarding.</summary>
    public bool IsConnecting => !IsReady && !IsEngineDown && !IsOnboarding;

    partial void OnIsReadyChanged(bool value) => OnPropertyChanged(nameof(IsConnecting));
    partial void OnIsEngineDownChanged(bool value) => OnPropertyChanged(nameof(IsConnecting));
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
        }
        catch (Exception ex)
        {
            EnterEngineDown(ex.Message);
        }
    }

    /// <summary>Activate the saved default engine, else the first connected real one, else engine-down.</summary>
    private async Task ConnectPreferredAsync()
    {
        // The screenshot renderer boots straight into its (single) demo provider, whatever
        // identity it presents, so captures never depend on a real Docker/Podman socket.
        if (Environment.GetEnvironmentVariable("KONTENA_SCREENSHOT") == "1")
        {
            var demo = _probes.FirstOrDefault(p => p.Connected) ?? (_probes.Count > 0 ? _probes[0] : null);
            if (demo is not null) { await ActivateAsync(demo.Provider); return; }
        }

        var real = _probes.FirstOrDefault(p =>
                       p.Connected && p.Provider.Backend != FakeBackend
                       && p.Provider.Backend == _settings.DefaultEngine)
                   ?? _probes.FirstOrDefault(p => p.Connected && p.Provider.Backend != FakeBackend);

        if (real is null)
        {
            EnterEngineDown("No Docker or Podman socket answered. The engine may be stopped, still starting, or you may not have permission to access it.");
            return;
        }

        await ActivateAsync(real.Provider);
    }

    private void EnterOnboarding()
    {
        IsReady = false;
        IsEngineDown = false;
        CurrentPage = null;
        Onboarding = new OnboardingViewModel(
            _probes,
            FakeBackend,
            _settings.AutoDetectEngines,
            onContinue: backend => _ = CompleteOnboardingAsync(backend),
            onSkip: () => _ = CompleteOnboardingAsync(null),
            onInstallPodman: () => Browser.OpenUrl("https://podman.io/docs/installation"));
        IsOnboarding = true;
    }

    private async Task CompleteOnboardingAsync(string? backend)
    {
        var autoDetect = Onboarding?.AutoDetect ?? _settings.AutoDetectEngines;
        _settings = _settings with
        {
            Onboarded = true,
            DefaultEngine = backend ?? _settings.DefaultEngine,
            AutoDetectEngines = autoDetect,
        };
        _store.Save(_settings);
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

    private void EnterEngineDown(string detail)
    {
        IsReady = false;
        IsEngineDown = true;
        EngineDownDetail = detail;
        EngineName = "No engine";
        EngineChip = "!";
        EngineDetail = "not connected";
        EngineEndpoint = string.Empty;
        CurrentPage = null;
    }

    [RelayCommand]
    private async Task ReconnectAsync()
    {
        IsEngineDown = false;
        EngineDownDetail = string.Empty;
        await InitAsync();
    }

    private async Task ActivateAsync(IBackendProvider provider)
    {
        Containers?.StopWatching();
        _activityLog.Detach();
        (_engine as IDisposable)?.Dispose();

        IsReady = false;
        IsEngineDown = false;
        _engine = provider.CreateEngine();
        _activeBackend = provider.Backend;
        EngineName = provider.DisplayName;
        EngineChip = provider.Chip;

        RebuildEngineList();

        DisposeDetail();
        CloseDialog();

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
        Volumes = new VolumesViewModel(_engine);
        Networks = new NetworksViewModel(_engine);
        ComposeProjects = new ComposeProjectsViewModel(_engine)
        {
            RequestOpenDetail = ShowContainerDetail,
            RequestNewProject = ShowComposeUpDialog,
            RequestProjectLogs = ShowComposeLogsDialog,
        };
        Activity = new ActivityViewModel(_activityLog);

        SearchText = string.Empty;
        CurrentPage = Containers;
        foreach (var item in NavItems)
            item.IsSelected = item.Key == "containers";

        await Containers.LoadAsync();
        IsReady = true;
        Containers.StartWatching();
        _activityLog.Attach(_engine, _activeBackend, ResolveEventName);

        await UpdateNavCountsAsync();
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
        var engines = _probes.Select(p => new EngineListItem(
            p.Provider.Backend, p.Provider.DisplayName, p.Provider.Chip,
            p.Detail ?? string.Empty, p.Connected,
            p.Provider.Backend == _settings.DefaultEngine)).ToList();

        SettingsPage = new SettingsViewModel(_store, _settings, engines);
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

        _settings = _settings with { Theme = next };

        if (SettingsPage is not null)
            SettingsPage.Theme = next; // applies + persists via its own handler
        else
        {
            ThemeApplier.Apply(next);
            _store.Save(_settings);
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
            initialImage: initialImage);
    }

    private void ShowPullDialog()
    {
        if (_engine is null)
            return;

        Dialog = new PullImageViewModel(_engine, CloseDialog, onPulled: RefreshAfterPullAsync);
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

        var recent = new List<string> { path };
        recent.AddRange(_settings.RecentBuildContexts
            .Where(c => !string.Equals(c, path, StringComparison.Ordinal)));

        _settings = _settings with { RecentBuildContexts = recent.Take(6).ToList() };
        _store.Save(_settings);
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
        Containers?.Dispose();
        _activityLog.Dispose();
        (_engine as IDisposable)?.Dispose();
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
            Engines.Add(new EngineOption
            {
                Backend = probe.Provider.Backend,
                Name = probe.Provider.DisplayName,
                Chip = probe.Provider.Chip,
                Detail = probe.Detail ?? string.Empty,
                IsActive = isActive,
                IsConnected = probe.Connected,
                SwitchCommand = probe.Connected && !isActive ? SwitchEngineCommand : null,
            });
        }
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
