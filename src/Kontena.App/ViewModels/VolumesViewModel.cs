using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Models;
using Kontena.Engines;

namespace Kontena.App.ViewModels;

public sealed partial class VolumesViewModel : ViewModelBase, IListPage
{
    private readonly IContainerEngine _engine;
    private readonly List<VolumeRowViewModel> _all = [];

    public VolumesViewModel(IContainerEngine engine) => _engine = engine;

    public ObservableCollection<VolumeRowViewModel> Items { get; } = [];

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _hasLoaded;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private bool _hasDangling;
    [ObservableProperty] private bool _pruneArmed;
    [ObservableProperty] private string _pruneSummary = string.Empty;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    public async Task LoadAsync()
    {
        var list = await _engine.ListVolumesAsync();
        _all.Clear();
        foreach (var volume in list.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
            _all.Add(new VolumeRowViewModel(volume, this));

        var dangling = list.Where(v => v.IsDangling).ToList();
        Summary = $"{list.Count} volumes · {dangling.Count} dangling";

        HasDangling = dangling.Count > 0;
        var reclaim = Format.Size(dangling.Sum(v => v.SizeBytes ?? 0));
        PruneSummary = $"Remove {dangling.Count} dangling volume{(dangling.Count == 1 ? "" : "s")} and free ~{reclaim}?";
        if (!HasDangling)
            PruneArmed = false;

        HasLoaded = true;
        ApplyFilter();
    }

    [RelayCommand]
    private void ArmPrune() => PruneArmed = HasDangling;

    [RelayCommand]
    private void CancelPrune() => PruneArmed = false;

    [RelayCommand]
    private async Task PruneAsync()
    {
        PruneArmed = false;
        try { await _engine.PruneVolumesAsync(); }
        catch { /* nothing to prune or engine hiccup */ }
        await LoadAsync();
    }

    public async Task DeleteAsync(string name)
    {
        try { await _engine.RemoveVolumeAsync(name, force: true); }
        catch { /* in-use or already gone */ }
        await LoadAsync();
    }

    private void ApplyFilter()
    {
        Items.Clear();
        foreach (var row in _all.Where(Matches))
            Items.Add(row);
    }

    private bool Matches(VolumeRowViewModel row)
        => string.IsNullOrWhiteSpace(SearchText)
        || row.Name.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase);
}
