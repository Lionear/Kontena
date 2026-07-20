using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Engines;
using Kontena.Engines.Fakes;

namespace Kontena.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly EngineRegistry _registry;
    private IReadOnlyList<EngineProbe> _probes = [];
    private IContainerEngine? _engine;
    private string _activeBackend = string.Empty;

    /// <summary>Design-time / default ctor uses a fake-only registry.</summary>
    public MainWindowViewModel()
        : this(new EngineRegistry([new FakeEngineProvider()]))
    {
    }

    public MainWindowViewModel(EngineRegistry registry)
    {
        _registry = registry;

        NavItems =
        [
            new NavItem("containers", "Containers", "IconContainer") { IsSelected = true },
            new NavItem("images", "Images", "IconLayers"),
            new NavItem("volumes", "Volumes", "IconDatabase"),
            new NavItem("networks", "Networks", "IconNetwork"),
        ];
        foreach (var item in NavItems)
            item.Command = NavigateCommand;

        _ = InitAsync();
    }

    // Pages
    [ObservableProperty] private ContainersViewModel? _containers;
    [ObservableProperty] private ImagesViewModel? _images;
    [ObservableProperty] private VolumesViewModel? _volumes;
    [ObservableProperty] private NetworksViewModel? _networks;

    /// <summary>The page shown in the content area.</summary>
    [ObservableProperty] private object? _currentPage;

    public ObservableCollection<NavItem> NavItems { get; }

    /// <summary>Engines shown in the backend-switcher dropdown.</summary>
    public ObservableCollection<EngineOption> Engines { get; } = [];

    [ObservableProperty] private string _engineName = "Connecting…";
    [ObservableProperty] private string _engineChip = "?";

    /// <summary>False until the first page is on screen (drives the connecting state).</summary>
    [ObservableProperty] private bool _isReady;

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
            "containers" => Containers,
            _ => Containers,
        };
        if (page is null)
            return;

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
            var active = _probes.FirstOrDefault(p => p.Connected) ?? _probes[^1];
            await ActivateAsync(active.Provider);
        }
        catch (Exception ex)
        {
            EngineName = "Engine unavailable";
            EngineChip = "!";
            System.Diagnostics.Debug.WriteLine($"Kontena init failed: {ex}");
        }
    }

    private async Task ActivateAsync(IEngineProvider provider)
    {
        Containers?.StopWatching();
        (_engine as IDisposable)?.Dispose();

        IsReady = false;
        _engine = provider.CreateEngine();
        _activeBackend = provider.Backend;
        EngineName = provider.DisplayName;
        EngineChip = provider.Chip;

        RebuildEngineList();

        Containers = new ContainersViewModel(_engine);
        Images = new ImagesViewModel(_engine);
        Volumes = new VolumesViewModel(_engine);
        Networks = new NetworksViewModel(_engine);

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
    }
}
