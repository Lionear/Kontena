using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Models;
using Kontena.Engines;

namespace Kontena.App.ViewModels;

public sealed partial class NetworksViewModel : ViewModelBase, IListPage
{
    private readonly IContainerEngine _engine;
    private readonly List<NetworkRowViewModel> _all = [];

    public NetworksViewModel(IContainerEngine engine) => _engine = engine;

    public ObservableCollection<NetworkRowViewModel> Items { get; } = [];

    /// <summary>Raised when a row's attachments are opened; the shell shows the dialog (KON-115).</summary>
    public Action<NetworkSummary>? RequestNetworkAttachments { get; set; }

    /// <summary>Raised when New network is clicked; the shell shows the modal (KON-92).</summary>
    public Action? RequestCreateNetwork { get; set; }

    [RelayCommand]
    private void CreateNetwork() => RequestCreateNetwork?.Invoke();

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _hasLoaded;
    [ObservableProperty] private string _summary = string.Empty;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    public async Task LoadAsync()
    {
        var list = await _engine.ListNetworksAsync();
        _all.Clear();
        foreach (var network in list.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
            _all.Add(new NetworkRowViewModel(network, this));

        var custom = list.Count(n => !n.IsBuiltIn);
        Summary = $"{list.Count} networks · {custom} custom";

        HasLoaded = true;
        ApplyFilter();
    }

    public async Task DeleteAsync(string id)
    {
        try { await _engine.RemoveNetworkAsync(id); }
        catch { /* built-in or in-use */ }
        await LoadAsync();
    }

    private void ApplyFilter()
    {
        Items.Clear();
        foreach (var row in _all.Where(Matches))
            Items.Add(row);
    }

    private bool Matches(NetworkRowViewModel row)
        => string.IsNullOrWhiteSpace(SearchText)
        || row.Name.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase);
}
