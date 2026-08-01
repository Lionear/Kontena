using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Sdk.Models;
using Kontena.Sdk;
using Kontena.Core.Orchestration;

namespace Kontena.App.ViewModels;

public sealed partial class NetworksViewModel : ListPageViewModel<NetworkRowViewModel>
{
    public override string SearchPlaceholder => "Search networks…";

    private readonly IContainerEngine _engine;

    public NetworksViewModel(IContainerEngine engine) => _engine = engine;

    /// <summary>Raised when a row's attachments are opened; the shell shows the dialog (KON-115).</summary>
    public Action<NetworkSummary>? RequestNetworkAttachments { get; set; }

    /// <summary>Raised when New network is clicked; the shell shows the modal (KON-92).</summary>
    public Action? RequestCreateNetwork { get; set; }

    [RelayCommand]
    private void CreateNetwork() => RequestCreateNetwork?.Invoke();

    [ObservableProperty] private string _summary = string.Empty;

    protected override async Task<IReadOnlyList<NetworkRowViewModel>> LoadRowsAsync()
    {
        var list = await _engine.ListNetworksAsync();

        var custom = list.Count(n => !n.IsBuiltIn);
        Summary = $"{list.Count} networks · {custom} custom";

        return [.. list
            .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .Select(n => new NetworkRowViewModel(n, this))];
    }

    /// <summary>
    /// Ask before deleting a network (KON-126). Nothing is lost that cannot be recreated, but attached
    /// containers lose the network they talk over — which is what the message leads with.
    /// </summary>
    public void ConfirmDelete(NetworkRowViewModel row)
    {
        var attached = row.AttachedCount > 0
            ? $" {row.AttachedCount} container{(row.AttachedCount == 1 ? "" : "s")} attached to it lose" +
              " this network, and the engine may refuse while they are running."
            : string.Empty;

        Confirm(
            "Delete network",
            $"Delete network \"{row.Name}\"? It can be created again, with a new subnet.{attached}",
            "Delete",
            () => DeleteAsync(row.Id));
    }

    public async Task DeleteAsync(string id)
    {
        try { await _engine.RemoveNetworkAsync(id); }
        catch { /* built-in or in-use */ }
        await LoadAsync();
    }

    protected override bool Matches(NetworkRowViewModel row, string term) => Contains(row.Name, term);
}
