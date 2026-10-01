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
public sealed partial class ClusterResourcesViewModel : ViewModelBase, IListPage, IDisposable
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
    private CancellationTokenSource? _watch;

    /// <param name="group">
    /// Show only this API group's kinds — what a sidebar entry under Custom resources opens (KON-483).
    /// Null for every kind the cluster serves.
    /// </param>
    /// <param name="kind">The kind to land on, rather than the first one there is.</param>
    public ClusterResourcesViewModel(
        IClusterEngine cluster, string? @namespace, string? group = null, string? kind = null)
    {
        _cluster = cluster;
        _namespace = @namespace;
        Group = group;
        _initialKind = kind;
        _ = LoadKindsAsync();
    }

    private readonly string? _initialKind;

    /// <summary>The API group this page is limited to, or null for all of them.</summary>
    public string? Group { get; }

    /// <summary>The page title: the group when the sidebar opened one, else the whole browser.</summary>
    public string Title => Group ?? "Resources";

    /// <summary>
    /// Whether the listing follows the cluster (KON-483). It used to be one read when a kind was
    /// picked, so a status column — the reason most custom resources declare columns at all — showed
    /// what was true at that moment for as long as the page stayed open.
    /// </summary>
    [ObservableProperty] private bool _isLive;

    /// <summary>Why it is not live, when it is not; never silent, for the reason IClusterLivePage gives.</summary>
    [ObservableProperty] private string? _liveNotice;

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

    /// <summary>
    /// Opens an object's detail page on a tab (KON-483) — the shell's, which knows which page a kind
    /// has. A row used to open a side panel with the YAML and nothing else.
    /// </summary>
    public Action<ResourceRef, string>? RequestOpenDetail { get; set; }

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

        OnPropertyChanged(nameof(CanDeleteSelected));
        _ = LoadTableAsync();
        Follow();
    }

    /// <summary>
    /// Follow the picked kind, the way every other list page follows its own (KON-250) — through the
    /// generic watch for a kind with no typed one (KON-483).
    /// </summary>
    private void Follow()
    {
        StopFollowing();

        if (Selected is not { } item)
            return;

        _watch = ClusterWatch.Follow(
            _cluster, [item.Resource.Kind], item.Resource.Namespaced ? _namespace : null,
            () => LoadTableAsync(quiet: true),
            (live, notice) =>
            {
                IsLive = live;
                LiveNotice = notice;
            });
    }

    private void StopFollowing()
    {
        _watch?.Cancel();
        _watch?.Dispose();
        _watch = null;
    }

    /// <summary>Stop following; cluster pages are rebuilt on every visit.</summary>
    public void Dispose()
    {
        StopFollowing();
        GC.SuppressFinalize(this);
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
        Selected = Groups.SelectMany(g => g.Items).FirstOrDefault(i => i.Kind == _initialKind)
                   ?? Groups.SelectMany(g => g.Items).FirstOrDefault(i => i.Kind == "ConfigMap")
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
            .Where(r => Group is null || r.Kind.Group == Group)
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

    /// <param name="quiet">
    /// A re-read the watch asked for: no spinner over rows that are about to come back the same, and a
    /// failed one leaves them standing rather than emptying the grid.
    /// </param>
    private async Task LoadTableAsync(bool quiet = false)
    {
        if (Selected is not { } item)
            return;

        IsLoading = !quiet;
        if (!quiet)
            Error = null;

        try
        {
            var table = await _cluster.ListTableAsync(
                item.Resource.Kind,
                item.Resource.Namespaced ? _namespace : null);

            // The kind may have been changed while this was out; its answer is not this listing's.
            if (ReferenceEquals(Selected, item))
                Table = table;
        }
        catch (Exception ex) when (!quiet)
        {
            Table = ResourceTable.Empty;
            Error = ex.Message;
        }
        catch (Exception)
        {
            // Quiet: the rows on screen stay, and the next event tries again.
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

    /// <summary>Open one object's detail page, on the tab asked for (KON-483).</summary>
    public void OpenDetail(ResourceRow row, string tab = "overview") =>
        RequestOpenDetail?.Invoke(row.Reference, tab);

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
