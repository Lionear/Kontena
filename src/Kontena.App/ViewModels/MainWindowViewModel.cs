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
            new NavItem("Containers", "IconContainer", isSelected: true),
            new NavItem("Images", "IconLayers"),
            new NavItem("Volumes", "IconDatabase"),
            new NavItem("Networks", "IconNetwork"),
        ];

        _ = InitAsync();
    }

    [ObservableProperty]
    private ContainersViewModel? _containers;

    public ObservableCollection<NavItem> NavItems { get; }

    /// <summary>Engines shown in the backend-switcher dropdown.</summary>
    public ObservableCollection<EngineOption> Engines { get; } = [];

    [ObservableProperty]
    private string _engineName = "Connecting…";

    [ObservableProperty]
    private string _engineChip = "?";

    /// <summary>False until the first container list is on screen (drives the loading state).</summary>
    [ObservableProperty]
    private bool _isReady;

    private async Task InitAsync()
    {
        try
        {
            _probes = await _registry.ProbeAllAsync();

            // First connected provider wins; the fake always connects as a fallback.
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
        // Tear down the previous engine/watcher, if any.
        Containers?.StopWatching();
        (_engine as IDisposable)?.Dispose();

        IsReady = false;
        _engine = provider.CreateEngine();
        _activeBackend = provider.Backend;
        EngineName = provider.DisplayName;
        EngineChip = provider.Chip;

        RebuildEngineList();

        Containers = new ContainersViewModel(_engine);
        await Containers.LoadAsync();
        IsReady = true;
        Containers.StartWatching();

        await UpdateNavCountsAsync();
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
