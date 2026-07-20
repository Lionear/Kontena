using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Kontena.Engines;
using Kontena.Engines.Fakes;

namespace Kontena.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IContainerEngine[] _candidates;
    private IContainerEngine? _engine;

    /// <summary>Design-time / default ctor uses the in-memory FakeEngine.</summary>
    public MainWindowViewModel() : this(new FakeEngine())
    {
    }

    /// <summary>
    /// Takes one or more candidate engines and connects to the first reachable one
    /// (falling back to the last). Lets the app try real Docker, then FakeEngine.
    /// </summary>
    public MainWindowViewModel(params IContainerEngine[] candidates)
    {
        _candidates = candidates.Length > 0 ? candidates : [new FakeEngine()];

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

    private async Task InitAsync()
    {
        try
        {
            _engine = await SelectEngineAsync();

            var info = await _engine.GetInfoAsync();
            EngineName = info.DisplayName;
            EngineChip = info.DisplayName.Length > 0 ? info.DisplayName[..1].ToUpperInvariant() : "?";

            Engines.Clear();
            Engines.Add(new EngineOption
            {
                Name = info.DisplayName,
                Chip = EngineChip,
                Detail = $"{info.Version} · {info.Endpoint}",
                IsActive = true,
            });

            Containers = new ContainersViewModel(_engine);
            await Containers.LoadAsync();
            Containers.StartWatching();

            var ci = CultureInfo.InvariantCulture;
            NavItems[0].Count = Containers.Items.Count.ToString(ci);
            NavItems[1].Count = (await _engine.ListImagesAsync()).Count.ToString(ci);
            NavItems[2].Count = (await _engine.ListVolumesAsync()).Count.ToString(ci);
            NavItems[3].Count = (await _engine.ListNetworksAsync()).Count.ToString(ci);
        }
        catch (Exception ex)
        {
            // Never let init crash the app; show a degraded header instead.
            EngineName = "Engine unavailable";
            EngineChip = "!";
            System.Diagnostics.Debug.WriteLine($"Kontena init failed: {ex}");
        }
    }

    /// <summary>Ping each candidate; use the first that answers, else the last.</summary>
    private async Task<IContainerEngine> SelectEngineAsync()
    {
        foreach (var candidate in _candidates)
        {
            try
            {
                await candidate.PingAsync();
                return candidate;
            }
            catch
            {
                // Not reachable — try the next candidate.
            }
        }

        return _candidates[^1];
    }
}
