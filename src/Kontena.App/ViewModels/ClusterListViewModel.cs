using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Kontena.App.ViewModels;

/// <summary>
/// A searchable cluster list (KON-164). Every cluster page was <c>: ViewModelBase</c> with no
/// <c>SearchText</c>, so the shell's <c>is IListPage</c> check was always false and the search box
/// took text and did nothing.
/// <para>
/// Written once rather than six times: the rows differ, the filtering does not — load into a backing
/// list, project the matches into the bound one. Six copies of that is six chances for one page to
/// filter case-sensitively or forget to re-apply after a reload.
/// </para>
/// </summary>
public abstract partial class ClusterListViewModel<TRow> : ViewModelBase, IListPage
{
    private readonly List<TRow> _all = [];

    /// <summary>The rows the view binds to — the matches, not everything.</summary>
    public ObservableCollection<TRow> Items { get; } = [];

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _hasLoaded;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    public abstract string SearchPlaceholder { get; }

    public async Task LoadAsync()
    {
        var rows = await LoadRowsAsync();

        _all.Clear();
        _all.AddRange(rows);
        HasLoaded = true;

        // Re-applied on every load, so a refresh under an active search does not quietly show
        // everything again.
        ApplyFilter();
    }

    /// <summary>Fetch this page's rows. Already scoped to the namespace picker by the caller.</summary>
    protected abstract Task<IReadOnlyList<TRow>> LoadRowsAsync();

    /// <summary>Whether a row matches the trimmed, non-empty search term.</summary>
    protected abstract bool Matches(TRow row, string term);

    /// <summary>Whether there is a list to draw at all — false hides the table rather than leaving a
    /// header floating above a "no matches" line.</summary>
    public bool HasItems => Items.Count > 0;

    /// <summary>Nothing matched a search that was actually typed — different from an empty page.</summary>
    public bool HasNoMatches => HasLoaded && Items.Count == 0 && _all.Count > 0;

    /// <summary>The page itself is empty, search or no search.</summary>
    public bool IsEmpty => HasLoaded && _all.Count == 0;

    /// <summary>
    /// Reconcile the bound list towards the matches, rather than clearing and refilling it.
    /// <para>
    /// <c>Clear()</c> raises a Reset, and a Reset makes the list throw away every row's visuals —
    /// grid, buttons, icons, the lot — and build them again, including for rows that were already on
    /// screen and still match. Narrowing a search touches a few rows; rebuilding all of them for each
    /// keystroke is where the typing lag came from, not from the matching.
    /// </para>
    /// <para>
    /// Both sequences keep the order of <c>_all</c>, so after the removals the bound list is a
    /// subsequence of the matches and each missing row belongs exactly at its own index.
    /// </para>
    /// </summary>
    private void ApplyFilter()
    {
        var term = SearchText.Trim();
        List<TRow> matches = term.Length == 0 ? [.. _all] : [.. _all.Where(r => Matches(r, term))];

        var keep = new HashSet<TRow>(matches);
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Items[i]))
                Items.RemoveAt(i);
        }

        for (var i = 0; i < matches.Count; i++)
        {
            if (i < Items.Count && EqualityComparer<TRow>.Default.Equals(Items[i], matches[i]))
                continue;

            Items.Insert(i, matches[i]);
        }

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasNoMatches));
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Case-insensitive contains, the comparison every one of these pages wants.</summary>
    protected static bool Contains(string? value, string term) =>
        value is not null && value.Contains(term, StringComparison.OrdinalIgnoreCase);
}
