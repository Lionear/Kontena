using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Models;
using Kontena.Engines;

namespace Kontena.App.ViewModels;

public sealed partial class ImagesViewModel : ViewModelBase, IListPage
{
    private readonly IContainerEngine _engine;
    private readonly List<ImageRowViewModel> _all = [];

    public ImagesViewModel(IContainerEngine engine) => _engine = engine;

    /// <summary>Raised when the Pull image button is clicked; the shell shows the Pull modal.</summary>
    public Action? RequestPullImage { get; set; }

    [RelayCommand]
    private void PullImage() => RequestPullImage?.Invoke();

    /// <summary>Raised when the Build image button is clicked; the shell shows the Build modal.</summary>
    public Action? RequestBuildImage { get; set; }

    [RelayCommand]
    private void BuildImage() => RequestBuildImage?.Invoke();

    public ObservableCollection<ImageRowViewModel> Items { get; } = [];

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _hasLoaded;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private bool _hasUnused;
    [ObservableProperty] private bool _pruneArmed;
    [ObservableProperty] private string _pruneSummary = string.Empty;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    public async Task LoadAsync()
    {
        var list = await _engine.ListImagesAsync();
        _all.Clear();
        foreach (var image in list.OrderBy(i => i.Repository, StringComparer.OrdinalIgnoreCase)
                                   .ThenBy(i => i.Tag, StringComparer.OrdinalIgnoreCase))
        {
            _all.Add(new ImageRowViewModel(image, this));
        }

        var total = list.Sum(i => i.SizeBytes);
        var unused = list.Where(i => !i.InUse).ToList();
        Summary = $"{list.Count} images · {Format.Size(total)} on disk · {unused.Count} unused";

        HasUnused = unused.Count > 0;
        var reclaim = Format.Size(unused.Sum(i => i.SizeBytes));
        PruneSummary = $"Remove {unused.Count} unused image{(unused.Count == 1 ? "" : "s")} and free ~{reclaim}?";
        if (!HasUnused)
            PruneArmed = false;

        HasLoaded = true;
        ApplyFilter();
    }

    [RelayCommand]
    private void ArmPrune() => PruneArmed = HasUnused;

    [RelayCommand]
    private void CancelPrune() => PruneArmed = false;

    [RelayCommand]
    private async Task PruneAsync()
    {
        PruneArmed = false;
        try { await _engine.PruneImagesAsync(allUnused: true); }
        catch { /* nothing to prune or engine hiccup */ }
        await LoadAsync();
    }

    public async Task DeleteAsync(string id)
    {
        try { await _engine.RemoveImageAsync(id, force: true); }
        catch { /* in-use or already gone — the reload reflects reality */ }
        await LoadAsync();
    }

    private void ApplyFilter()
    {
        Items.Clear();
        foreach (var row in _all.Where(Matches))
            Items.Add(row);
    }

    private bool Matches(ImageRowViewModel row)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
            return true;

        var q = SearchText.Trim();
        return row.RepoName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || row.RepoNamespace.Contains(q, StringComparison.OrdinalIgnoreCase)
            || row.Tag.Contains(q, StringComparison.OrdinalIgnoreCase);
    }
}
