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
    private readonly EngineRegistry _registry;
    private readonly SettingsStore _store;
    private KontenaSettings _settings;
    private IReadOnlyList<EngineProbe> _probes = [];
    private IContainerEngine? _engine;
    private string _activeBackend = string.Empty;
    private ContainerDetailViewModel? _detail;

    /// <summary>Design-time / default ctor uses a fake-only registry.</summary>
    public MainWindowViewModel()
        : this(new EngineRegistry([new FakeEngineProvider()]))
    {
    }

    public MainWindowViewModel(EngineRegistry registry)
        : this(registry, new SettingsStore(), new KontenaSettings())
    {
    }

    public MainWindowViewModel(EngineRegistry registry, SettingsStore store, KontenaSettings settings)
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

    /// <summary>The page shown in the content area.</summary>
    [ObservableProperty] private object? _currentPage;

    /// <summary>The active modal dialog (e.g. Run container), or null when none.</summary>
    [ObservableProperty] private object? _dialog;

    public bool IsDialogOpen => Dialog is not null;

    partial void OnDialogChanged(object? value) => OnPropertyChanged(nameof(IsDialogOpen));

    public ObservableCollection<NavItem> NavItems { get; }

    /// <summary>Engines shown in the backend-switcher dropdown.</summary>
    public ObservableCollection<EngineOption> Engines { get; } = [];

    [ObservableProperty] private string _engineName = "Connecting…";
    [ObservableProperty] private string _engineChip = "?";

    /// <summary>False until the first page is on screen (drives the connecting state).</summary>
    [ObservableProperty] private bool _isReady;

    /// <summary>True when no container engine could be reached — shows the engine-down state.</summary>
    [ObservableProperty] private bool _isEngineDown;

    [ObservableProperty] private string _engineDownDetail = string.Empty;

    /// <summary>The connecting state shows only while neither ready nor engine-down.</summary>
    public bool IsConnecting => !IsReady && !IsEngineDown;

    partial void OnIsReadyChanged(bool value) => OnPropertyChanged(nameof(IsConnecting));
    partial void OnIsEngineDownChanged(bool value) => OnPropertyChanged(nameof(IsConnecting));

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
        catch (Exception ex)
        {
            EnterEngineDown(ex.Message);
        }
    }

    private void EnterEngineDown(string detail)
    {
        IsReady = false;
        IsEngineDown = true;
        EngineDownDetail = detail;
        EngineName = "No engine";
        EngineChip = "!";
        CurrentPage = null;
    }

    [RelayCommand]
    private async Task ReconnectAsync()
    {
        IsEngineDown = false;
        EngineDownDetail = string.Empty;
        await InitAsync();
    }

    private async Task ActivateAsync(IEngineProvider provider)
    {
        Containers?.StopWatching();
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
        ComposeProjects = new ComposeProjectsViewModel(_engine) { RequestOpenDetail = ShowContainerDetail };

        SearchText = string.Empty;
        CurrentPage = Containers;
        foreach (var item in NavItems)
            item.IsSelected = item.Key == "containers";

        await Containers.LoadAsync();
        IsReady = true;
        Containers.StartWatching();

        await UpdateNavCountsAsync();
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

        Dialog = new BuildImageViewModel(_engine, CloseDialog, onRun: image =>
        {
            CloseDialog();
            _ = ShowRunDialogAsync(image);
        });
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
