using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.ViewModels;

/// <summary>One kind in the picker.</summary>
public sealed partial class ApiResourceItem(ApiResource resource) : ObservableObject
{
    public ApiResource Resource { get; } = resource;

    public string Kind => Resource.Kind.Kind;

    /// <summary>The group, shown under the kind so two kinds of the same name stay apart.</summary>
    public string Group => Resource.Kind.Group.Length == 0 ? "core" : Resource.Kind.Group;

    /// <summary>
    /// The other names this kind answers to: its plural, its short names, and the categories it
    /// declares itself part of (KON-455). Shown under the kind because they are what someone who
    /// half-remembers a custom resource actually remembers — <c>cert</c>, not <c>Certificate</c> —
    /// and because a name you can search for but cannot see reads as a search that failed.
    /// </summary>
    public string Aliases => string.Join(" · ", Names(Resource).Skip(1));

    /// <summary>Everything this kind can be found by, the kind itself first.</summary>
    internal static IEnumerable<string> Names(ApiResource resource) =>
        new[] { resource.Kind.Kind, resource.Plural }
            .Concat(resource.ShortNames)
            .Concat(resource.Categories)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>What this kind is for, in the words of whoever wrote the CRD. Empty for built-ins.</summary>
    public string Description => Resource.Description;

    public bool HasDescription => Description.Length > 0;

    /// <summary>The group and version, with what installed the kind when the definition says.</summary>
    public string Origin => Resource.Source.Length == 0
        ? Group
        : $"{Group} · {Resource.Source}";

    /// <summary>
    /// Whether this kind is worth showing for a search term.
    /// <para>
    /// Every field it matches on is one the row displays, which is deliberate: a result whose reason
    /// for matching is nowhere on screen looks like a bug, and a page that has to explain its own hits
    /// with a badge is a page that searched something it did not show.
    /// </para>
    /// </summary>
    internal static bool Matches(ApiResource resource, string term) =>
        term.Length == 0
        || Names(resource).Any(n => n.Contains(term, StringComparison.OrdinalIgnoreCase))
        || resource.Kind.Group.Contains(term, StringComparison.OrdinalIgnoreCase)
        || resource.Source.Contains(term, StringComparison.OrdinalIgnoreCase)
        || resource.Description.Contains(term, StringComparison.OrdinalIgnoreCase);

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// One workload that uses, or was created by, the object being shown (KON-455). Clicking it opens
/// that workload — the same click-through a related pod row has had since the workload detail
/// existed, pointed the other way.
/// </summary>
public sealed partial class ResourceUsageRow(ResourceUsage usage, Action<ResourceRef>? onOpen)
{
    public ResourceRef Reference { get; } = usage.User;

    public string Name => Reference.Name;

    /// <summary>Kind and namespace, the way every other row in the app spells a location.</summary>
    public string Where => Reference.Namespace is { Length: > 0 } ns
        ? $"{Reference.Kind.Kind} · namespace {ns}"
        : Reference.Kind.Kind;

    /// <summary>
    /// Which way the relation runs. "Created by this" and "uses this" end up in the same list and are
    /// not the same fact — an operator's own StatefulSet is not a consumer of the object.
    /// </summary>
    public string Direction => usage.OwnedByTarget ? "created by this" : "uses this";

    /// <summary>
    /// How the link was found, in the cluster's own words. On the row because a relation whose basis
    /// is not on screen cannot be checked — and these bases are not equally strong.
    /// </summary>
    public string Evidence => usage.Evidence switch
    {
        UsageEvidence.OwnerReference => "ownerReference",
        UsageEvidence.Mount => $"mounts {usage.Detail}",
        _ => $"annotation {usage.Detail}",
    };

    [RelayCommand]
    private void Open() => onOpen?.Invoke(Reference);
}

/// <summary>A heading in the picker and the kinds under it.</summary>
public sealed class ApiResourceGroup(string title, IReadOnlyList<ApiResourceItem> items)
{
    public string Title { get; } = title;

    public IReadOnlyList<ApiResourceItem> Items { get; } = items;

    public bool HasItems => Items.Count > 0;
}

/// <summary>
/// Browse any kind the cluster serves, custom ones included (KON-75).
/// <para>
/// The kinds come from discovery and the columns from the API server, so this page knows nothing about
/// any particular resource. That is what lets it show the things Kontena has no screen for — ConfigMaps,
/// Secrets, RBAC, and every CRD an operator installed — without a screen each.
/// </para>
/// </summary>
public sealed partial class ClusterResourcesViewModel : ViewModelBase, IListPage
{
    /// <summary>
    /// Rows are laid out one grid cell at a time, so a namespace with thousands of objects would build
    /// thousands of controls. Cut off with the count said out loud: a list that silently stops at 500 is
    /// a list that lies about what is in the cluster.
    /// </summary>
    public const int RowLimit = 500;

    private readonly IClusterEngine _cluster;
    private readonly string? _namespace;
    private IReadOnlyList<ApiResource> _resources = [];

    public ClusterResourcesViewModel(IClusterEngine cluster, string? @namespace)
    {
        _cluster = cluster;
        _namespace = @namespace;
        _ = LoadKindsAsync();
    }

    /// <summary>The kinds on offer, grouped by where they came from.</summary>
    public ObservableCollection<ApiResourceGroup> Groups { get; } = [];

    [ObservableProperty] private string _kindSearch = string.Empty;

    /// <summary>
    /// Filters the objects in the listing, as opposed to <see cref="KindSearch"/> which filters the
    /// kinds in the picker (KON-454). Bound to the shared command-bar box like every other list page:
    /// this page was the one that left it greyed out, because it did not implement
    /// <see cref="IListPage"/> — the box was not missing, it was disabled.
    /// </summary>
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private ApiResourceItem? _selected;
    [ObservableProperty] private ResourceTable? _table;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isLoadingKinds = true;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _manifest;
    [ObservableProperty] private string? _manifestTitle;

    /// <summary>
    /// The workloads that use the object whose manifest is open, or that it created (KON-455).
    /// </summary>
    [ObservableProperty] private IReadOnlyList<ResourceUsageRow> _users = [];

    /// <summary>
    /// True once the question has been asked and answered, however it came out. Without it the empty
    /// state cannot tell "nothing uses this" from "not looked yet", and would flash the first while
    /// the second is still true.
    /// </summary>
    [ObservableProperty] private bool _usersChecked;

    /// <summary>Opens the workload behind a relation row. The shell owns navigation.</summary>
    public Action<ResourceRef>? RequestOpen { get; set; }

    /// <summary>The column currently sorted by, or null for the order the server sent.</summary>
    [ObservableProperty] private string? _sortColumn;

    [ObservableProperty] private bool _sortDescending;

    /// <summary>
    /// The rows on screen: what matches the search, in the sorted order, cut off at
    /// <see cref="RowLimit"/>. The view draws these rather than <see cref="Table"/>'s own rows.
    /// </summary>
    [ObservableProperty] private IReadOnlyList<ResourceRow> _rows = [];

    /// <summary>How many rows matched before the cut-off — what the truncation note counts.</summary>
    private int _matches;

    /// <summary>How many matching rows were left off the screen, so the page can say so.</summary>
    public int Hidden => Math.Max(0, _matches - RowLimit);

    public bool IsTruncated => Hidden > 0;

    public string TruncatedNote =>
        $"Showing the first {RowLimit.ToString(CultureInfo.InvariantCulture)} of "
        + _matches.ToString(CultureInfo.InvariantCulture)
        + ". Narrow it down with the search box or the namespace picker.";

    /// <summary>
    /// Nothing matched a search that was actually typed — a different situation from a kind with no
    /// objects in it, and one the page has to say out loud rather than showing an empty grid.
    /// </summary>
    public bool HasNoMatches =>
        !IsLoading && Error is null && Rows.Count == 0 && Table is { Rows.Count: > 0 };

    /// <inheritdoc/>
    public bool HasLoaded => Table is not null;

    /// <inheritdoc/>
    public string SearchPlaceholder => "Search this listing…";

    /// <inheritdoc/>
    public Task LoadAsync() => LoadTableAsync();

    public bool CanDeleteSelected => Selected?.Resource.CanDelete == true;

    public bool HasUsers => Users.Count > 0;

    /// <summary>
    /// Nothing found, and what was looked at. Every comparable tool leaves this silent, which cannot
    /// be told apart from "nothing uses this" — and a generic ladder will sometimes miss a custom
    /// resource that links itself in a way none of the three rungs covers.
    /// </summary>
    public bool NothingUsesIt => UsersChecked && Users.Count == 0;

    /// <summary>True once there is nothing to show and nothing on its way.</summary>
    public bool IsEmpty => !IsLoading && Table is { Rows.Count: 0 } && Error is null;

    partial void OnKindSearchChanged(string value) => Regroup();

    partial void OnSearchTextChanged(string value) => RefreshRows();

    partial void OnTableChanged(ResourceTable? value) => RefreshRows();

    /// <summary>
    /// Sort by a column, or flip the direction if it is already the active one, then let go of the
    /// sort entirely on the third click — the same three states the cluster list pages have had since
    /// KON-318, so a header behaves the same wherever it is clicked.
    /// </summary>
    [RelayCommand]
    private void SortBy(string column)
    {
        if (IndexOf(column) < 0)
            return;

        if (SortColumn != column)
        {
            SortColumn = column;
            SortDescending = false;
        }
        else if (!SortDescending)
            SortDescending = true;
        else
        {
            SortColumn = null;
            SortDescending = false;
        }

        RefreshRows();
    }

    /// <summary>Where a column sits in the server's table, or -1 if this listing has no such column.</summary>
    private int IndexOf(string? column)
    {
        if (column is null || Table is not { } table)
            return -1;

        for (var i = 0; i < table.Columns.Count; i++)
        {
            if (string.Equals(table.Columns[i].Name, column, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    private static string CellAt(ResourceRow row, int index) =>
        index >= 0 && index < row.Cells.Count ? row.Cells[index] : string.Empty;

    /// <summary>
    /// Recompute what is on screen: filter, then sort, then cut off. In that order — sorting the whole
    /// listing to then throw most of it away is work nobody sees, and cutting off before sorting would
    /// sort the wrong five hundred rows.
    /// </summary>
    private void RefreshRows()
    {
        if (Table is not { } table)
        {
            _matches = 0;
            Rows = [];
            RaiseRowCounts();
            return;
        }

        var term = SearchText.Trim();
        IEnumerable<ResourceRow> matching = table.Rows;

        // Across every cell, not only the name: the columns a custom resource declares are the ones
        // worth searching, and this page cannot know which of them is the interesting one.
        if (term.Length > 0)
            matching = matching.Where(row => row.Cells.Any(cell => Contains(cell, term)));

        if (IndexOf(SortColumn) is var index && index >= 0)
        {
            matching = SortDescending
                ? matching.OrderByDescending(row => ResourceCellOrder.Of(CellAt(row, index)))
                : matching.OrderBy(row => ResourceCellOrder.Of(CellAt(row, index)));
        }

        var matches = matching.ToArray();
        _matches = matches.Length;
        Rows = matches.Length > RowLimit ? matches[..RowLimit] : matches;
        RaiseRowCounts();
    }

    private void RaiseRowCounts()
    {
        OnPropertyChanged(nameof(Hidden));
        OnPropertyChanged(nameof(IsTruncated));
        OnPropertyChanged(nameof(TruncatedNote));
        OnPropertyChanged(nameof(HasNoMatches));
    }

    private static bool Contains(string? value, string term) =>
        value is not null && value.Contains(term, StringComparison.OrdinalIgnoreCase);

    partial void OnSelectedChanged(ApiResourceItem? value)
    {
        foreach (var item in Groups.SelectMany(g => g.Items))
            item.IsSelected = ReferenceEquals(item, value);

        Manifest = null;
        OnPropertyChanged(nameof(CanDeleteSelected));
        _ = LoadTableAsync();
    }

    private async Task LoadKindsAsync()
    {
        try
        {
            _resources = await _cluster.DiscoverResourcesAsync();
        }
        catch (Exception ex)
        {
            Error = "Could not ask the cluster what it serves: " + ex.Message;
        }
        finally
        {
            IsLoadingKinds = false;
        }

        Regroup();

        // Land on something rather than an empty pane asking to be told what to look at.
        Selected = Groups.SelectMany(g => g.Items).FirstOrDefault(i => i.Kind == "ConfigMap")
                   ?? Groups.SelectMany(g => g.Items).FirstOrDefault();
    }

    private void Regroup()
    {
        // Against every name the kind answers to, not only the Kind and the group. Someone who knows
        // the object is in a custom resource but not what the type is called is exactly who this page
        // is for, and Kind-only matching is what made them scroll (KON-455).
        var term = KindSearch.Trim();

        var matching = _resources
            .Where(r => r.CanList)
            .Where(r => ApiResourceItem.Matches(r, term))
            .OrderBy(r => r.Kind.Kind, StringComparer.OrdinalIgnoreCase)
            .Select(r => new ApiResourceItem(r))
            .ToArray();

        Groups.Clear();

        // Custom first: the built-in kinds are largely the ones with a screen of their own already, and
        // what someone comes here for is the half of the cluster that has none.
        foreach (var group in new[]
                 {
                     new ApiResourceGroup("Custom resources", [.. matching.Where(i => i.Resource.IsCustom)]),
                     new ApiResourceGroup("Kubernetes", [.. matching.Where(i => !i.Resource.IsCustom)]),
                 })
        {
            if (group.HasItems)
                Groups.Add(group);
        }
    }

    private async Task LoadTableAsync()
    {
        if (Selected is not { } item)
            return;

        IsLoading = true;
        Error = null;

        try
        {
            Table = await _cluster.ListTableAsync(
                item.Resource.Kind,
                item.Resource.Namespaced ? _namespace : null);
        }
        catch (Exception ex)
        {
            Table = ResourceTable.Empty;
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(HasLoaded));
            OnPropertyChanged(nameof(IsEmpty));
            RaiseRowCounts();
        }
    }

    /// <summary>Re-read the current kind.</summary>
    [RelayCommand]
    public Task Refresh() => LoadTableAsync();

    /// <summary>Show one object's manifest, which is the same for every kind and needs no model.</summary>
    public async Task ShowManifestAsync(ResourceRow row)
    {
        ManifestTitle = row.Reference.Name;
        Manifest = "Loading…";
        Users = [];
        UsersChecked = false;
        RaiseUsers();

        _ = LoadUsersAsync(row.Reference);

        try
        {
            Manifest = await _cluster.GetManifestAsync(row.Reference);
        }
        catch (Exception ex)
        {
            Manifest = "# " + ex.Message;
        }
    }

    /// <summary>
    /// Ask the cluster what uses this object. Separate from the manifest read and not awaited with it:
    /// the manifest is one GET and this is several lists, and the YAML should not wait for them.
    /// </summary>
    private async Task LoadUsersAsync(ResourceRef reference)
    {
        IReadOnlyList<ResourceUsage> usages;

        try
        {
            usages = await _cluster.FindUsersAsync(reference);
        }
        catch (Exception)
        {
            // An engine that cannot answer leaves the section closed rather than the page broken.
            usages = [];
        }

        // The panel may have been closed, or moved to another object, while this was out.
        if (!string.Equals(ManifestTitle, reference.Name, StringComparison.Ordinal))
            return;

        Users = [.. usages.Select(u => new ResourceUsageRow(u, RequestOpen))];
        UsersChecked = true;
        RaiseUsers();
    }

    private void RaiseUsers()
    {
        OnPropertyChanged(nameof(HasUsers));
        OnPropertyChanged(nameof(NothingUsesIt));
    }

    /// <summary>Close the manifest panel.</summary>
    [RelayCommand]
    public void CloseManifest()
    {
        Manifest = null;
        Users = [];
        UsersChecked = false;
        RaiseUsers();
    }

    /// <summary>
    /// Delete an object, through the shell's confirm like every other destructive action (KON-126).
    /// Offered only where the API server says the verb exists, so the button is never one that could
    /// only fail.
    /// </summary>
    public void ConfirmDelete(ResourceRow row)
    {
        var reference = row.Reference;
        var where = reference.Namespace is { Length: > 0 } ns ? $" in {ns}" : string.Empty;

        Confirm(
            $"Delete {reference.Kind.Kind}",
            $"Delete {reference.Kind.Kind} \"{reference.Name}\"{where}? If something owns it, a "
            + "replacement may be created straight away; if not, it is gone for good.",
            "Delete",
            onConfirm: async () =>
            {
                await _cluster.DeleteAsync(reference);
                await LoadTableAsync();
            });
    }
}
