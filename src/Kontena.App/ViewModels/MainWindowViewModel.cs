using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Kontena.Engines;
using Kontena.Engines.Fakes;

namespace Kontena.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IContainerEngine _engine;

    /// <summary>Design-time / default ctor uses the in-memory FakeEngine.</summary>
    public MainWindowViewModel() : this(new FakeEngine())
    {
    }

    public MainWindowViewModel(IContainerEngine engine)
    {
        _engine = engine;
        Containers = new ContainersViewModel(engine);

        NavItems =
        [
            new NavItem("Containers", "IconContainer", isSelected: true),
            new NavItem("Images", "IconLayers"),
            new NavItem("Volumes", "IconDatabase"),
            new NavItem("Networks", "IconNetwork"),
        ];

        _ = InitAsync();
    }

    public ContainersViewModel Containers { get; }

    public ObservableCollection<NavItem> NavItems { get; }

    /// <summary>Engines shown in the backend-switcher dropdown.</summary>
    public ObservableCollection<EngineOption> Engines { get; } = [];

    [ObservableProperty]
    private string _engineName = "Connecting…";

    [ObservableProperty]
    private string _engineChip = "?";

    private async Task InitAsync()
    {
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

        await Containers.LoadAsync();

        NavItems[0].Count = Containers.Items.Count.ToString();
        NavItems[1].Count = (await _engine.ListImagesAsync()).Count.ToString();
        NavItems[2].Count = (await _engine.ListVolumesAsync()).Count.ToString();
        NavItems[3].Count = (await _engine.ListNetworksAsync()).Count.ToString();
    }
}
