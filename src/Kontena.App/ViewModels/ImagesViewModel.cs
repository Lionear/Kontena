using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Sdk.Models;
using Kontena.Sdk;
using Kontena.Core.Orchestration;

namespace Kontena.App.ViewModels;

public sealed partial class ImagesViewModel : ListPageViewModel<ImageRowViewModel>
{
    public override string SearchPlaceholder => "Search images…";

    private readonly IContainerEngine _engine;

    public ImagesViewModel(IContainerEngine engine) => _engine = engine;

    /// <summary>Raised when the Pull image button is clicked; the shell shows the Pull modal.</summary>
    public Action? RequestPullImage { get; set; }

    [RelayCommand]
    private void PullImage() => RequestPullImage?.Invoke();

    /// <summary>Raised when the Build image button is clicked; the shell shows the Build modal.</summary>
    public Action? RequestBuildImage { get; set; }

    /// <summary>Raised by a row's Tag and push action; the shell shows the modal for that image (KON-387).</summary>
    public Action<ImageRowViewModel>? RequestTagPushImage { get; set; }

    [RelayCommand]
    private void BuildImage() => RequestBuildImage?.Invoke();

    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private bool _hasUnused;
    [ObservableProperty] private bool _pruneArmed;
    [ObservableProperty] private string _pruneSummary = string.Empty;

    protected override async Task<IReadOnlyList<ImageRowViewModel>> LoadRowsAsync(CancellationToken ct)
    {
        var list = await _engine.ListImagesAsync(ct);

        var total = list.Sum(i => i.SizeBytes);
        var unused = list.Where(i => !i.InUse).ToList();
        Summary = $"{list.Count} images · {Format.Size(total)} on disk · {unused.Count} unused";

        HasUnused = unused.Count > 0;
        var reclaim = Format.Size(unused.Sum(i => i.SizeBytes));
        PruneSummary = $"Remove {unused.Count} unused image{(unused.Count == 1 ? "" : "s")} and free ~{reclaim}?";
        if (!HasUnused)
            PruneArmed = false;

        return [.. list
            .OrderBy(i => i.Repository, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Tag, StringComparer.OrdinalIgnoreCase)
            .Select(i => new ImageRowViewModel(i, this))];
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

    /// <summary>
    /// Ask before deleting an image (KON-126). An image is the one removal on these pages that is
    /// genuinely recoverable — you pull it again — so the message says so instead of threatening.
    /// </summary>
    public void ConfirmDelete(ImageRowViewModel row)
    {
        var inUse = row.InUse
            ? " A container is still using it, so the delete may be refused."
            : string.Empty;

        Confirm(
            "Delete image",
            $"Delete image \"{row.Reference}\"? It has to be pulled again before anything can run" +
            $" from it.{inUse}",
            "Delete",
            () => DeleteAsync(row.Id));
    }

    public async Task DeleteAsync(string id)
    {
        try { await _engine.RemoveImageAsync(id, force: true); }
        catch { /* in-use or already gone — the reload reflects reality */ }
        await LoadAsync();
    }

    protected override bool Matches(ImageRowViewModel row, string term) =>
        Contains(row.RepoName, term) || Contains(row.RepoNamespace, term) || Contains(row.Tag, term);
}
