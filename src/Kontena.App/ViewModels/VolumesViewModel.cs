using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Sdk.Models;
using Kontena.Sdk;
using Kontena.Core.Orchestration;

namespace Kontena.App.ViewModels;

public sealed partial class VolumesViewModel : ListPageViewModel<VolumeRowViewModel>
{
    public override string SearchPlaceholder => "Search volumes…";

    private readonly IContainerEngine _engine;

    public VolumesViewModel(IContainerEngine engine) => _engine = engine;

    /// <summary>Whether this engine can read a volume's contents at all (KON-90).</summary>
    public bool CanBrowse => _engine.Capabilities.SupportsVolumeBrowse;

    /// <summary>Raised when a row's Browse action is used; the shell shows the browser (KON-90).</summary>
    public Action<string>? RequestBrowseVolume { get; set; }

    /// <summary>Raised when New volume is clicked; the shell shows the modal (KON-91).</summary>
    public Action? RequestCreateVolume { get; set; }

    [RelayCommand]
    private void CreateVolume() => RequestCreateVolume?.Invoke();

    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private bool _hasDangling;
    [ObservableProperty] private bool _pruneArmed;
    [ObservableProperty] private string _pruneSummary = string.Empty;

    protected override async Task<IReadOnlyList<VolumeRowViewModel>> LoadRowsAsync()
    {
        var list = await _engine.ListVolumesAsync();

        var dangling = list.Where(v => v.IsDangling).ToList();
        Summary = $"{list.Count} volumes · {dangling.Count} dangling";

        HasDangling = dangling.Count > 0;
        var reclaim = Format.Size(dangling.Sum(v => v.SizeBytes ?? 0));
        // Same shape as the other prune banners, plus what is unique here: a pruned volume takes its
        // contents with it, and no other page's prune destroys data (KON-126).
        PruneSummary = $"Remove {dangling.Count} dangling volume{(dangling.Count == 1 ? "" : "s")}"
            + $" and free ~{reclaim}? Their contents go with them.";
        if (!HasDangling)
            PruneArmed = false;

        return [.. list
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .Select(v => new VolumeRowViewModel(v, this))];
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

    /// <summary>
    /// Ask before removing a volume (KON-126). A volume is the one thing on this page that holds data
    /// nothing else has a copy of, so the message says exactly that — and names what has it mounted,
    /// because "not mounted" is the difference between a safe delete and a broken container.
    /// </summary>
    public void ConfirmDelete(VolumeRowViewModel row)
    {
        var mounted = row.MountedBy.Count > 0
            ? $" It is mounted by {string.Join(", ", row.MountedBy)}, which will lose it."
            : string.Empty;

        Confirm(
            "Delete volume",
            $"Delete volume \"{row.Name}\"? Everything stored in it is removed with it and cannot be" +
            $" brought back.{mounted}",
            "Delete",
            () => DeleteAsync(row.Name));
    }

    public async Task DeleteAsync(string name)
    {
        try { await _engine.RemoveVolumeAsync(name, force: true); }
        catch { /* in-use or already gone */ }
        await LoadAsync();
    }

    protected override bool Matches(VolumeRowViewModel row, string term) => Contains(row.Name, term);
}
