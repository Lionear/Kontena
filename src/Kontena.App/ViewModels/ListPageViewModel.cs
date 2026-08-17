using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Orchestration;

namespace Kontena.App.ViewModels;

/// <summary>
/// A list page with a search box (KON-164, KON-189). Load into a backing list, project the matches
/// into the bound one, and tell the view which of the three empty situations it is in.
/// <para>
/// Written once rather than ten times: the rows differ, the filtering does not. It began on the
/// cluster side as <c>ClusterListViewModel</c>, which was never a cluster-specific idea — the engine
/// pages were the copies that kept <c>Items.Clear()</c>, and a Reset makes the list rebuild every
/// row's visuals, including the ones that were already on screen and still match. That is where the
/// typing lag came from; the debounce only spread it over fewer keystrokes.
/// </para>
/// </summary>
public abstract partial class ListPageViewModel<TRow> : ViewModelBase, IListPage
{
    private readonly List<TRow> _all = [];

    /// <summary>Everything loaded, matching or not — what a summary line counts.</summary>
    protected IReadOnlyList<TRow> All => _all;

    /// <summary>The rows the view binds to — the matches, not everything.</summary>
    public ObservableCollection<TRow> Items { get; } = [];

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _hasLoaded;

    /// <summary>
    /// Set for the duration of the first fetch (KON-319). A large cluster's list can take a real,
    /// visible while to come back — without this the page looks frozen rather than working, and "did
    /// it hang" is a worse question than a spinner answers for free.
    /// <para>
    /// Only the first fetch, not every one: a live cluster page reloads itself on every settled watch
    /// event (<see cref="ClusterListPageViewModel{TRow}"/>), which on an active cluster can be every
    /// few seconds. A spinner on each of those is not "loading", it is noise — the exact flicker
    /// <see cref="ListSync"/> exists to avoid on the rows themselves.
    /// </para>
    /// </summary>
    [ObservableProperty] private bool _isLoading;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    public abstract string SearchPlaceholder { get; }

    /// <summary>
    /// Virtual so a page can wrap every one of its own refreshes at once (KON-393). Manual refresh,
    /// a watch event and a poll all arrive here, and a page that needs to say something about a
    /// failed read wants to say it however the read was asked for — not only on the path its own
    /// ticket happened to add.
    /// </summary>
    public virtual async Task LoadAsync()
    {
        var isFirstLoad = !HasLoaded;
        if (isFirstLoad)
            IsLoading = true;

        try
        {
            var rows = await Services.Diag.TimeAsync($"{GetType().Name} fetch", LoadRowsAsync());

            _all.Clear();
            _all.AddRange(rows);
            HasLoaded = true;

            // Re-applied on every load, so a refresh under an active search does not quietly show
            // everything again.
            Services.Diag.Time($"{GetType().Name} rows onto the page", ApplyFilter);
        }
        finally
        {
            if (isFirstLoad)
                IsLoading = false;
        }
    }

    /// <summary>Fetch this page's rows, in the order they should appear.</summary>
    protected abstract Task<IReadOnlyList<TRow>> LoadRowsAsync();

    /// <summary>Whether a row matches the trimmed, non-empty search term.</summary>
    protected abstract bool Matches(TRow row, string term);

    /// <summary>
    /// A filter that is not the search box — a toggle the page owns, applied whether or not anything
    /// has been typed (KON-248: "warnings only" on the events page). Everything passes by default, so
    /// no existing page changes behaviour.
    /// </summary>
    protected virtual bool Include(TRow row) => true;

    /// <summary>
    /// Columns this page can be sorted by, keyed by the header text shown to the user (KON-318).
    /// Empty by default — a page that declares none is not sortable, rather than every page needing
    /// to opt out.
    /// </summary>
    protected virtual IReadOnlyDictionary<string, Func<TRow, IComparable>> SortColumns { get; } =
        new Dictionary<string, Func<TRow, IComparable>>(StringComparer.Ordinal);

    /// <summary>The column currently sorted by, or null for load order (newest-first pages, etc).</summary>
    [ObservableProperty] private string? _sortColumn;

    [ObservableProperty] private bool _sortDescending;

    /// <summary>
    /// Sort by a column, or flip direction if it is already the active one. The key comes off a
    /// clicked header, so an unknown one (a page mid-redesign) is ignored rather than thrown.
    /// </summary>
    [RelayCommand]
    private void SortBy(string column)
    {
        if (!SortColumns.ContainsKey(column))
            return;

        if (SortColumn == column)
            SortDescending = !SortDescending;
        else
        {
            SortColumn = column;
            SortDescending = false;
        }

        ApplyFilter();
    }

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
    /// A sort reorders the matches without adding or removing any, so the bound list is not a
    /// subsequence of them — <see cref="ListSync.Apply"/> has to handle a move, not only an insert
    /// at the right index (KON-374).
    /// </para>
    /// </summary>
    protected void ApplyFilter()
    {
        var term = SearchText.Trim();
        IEnumerable<TRow> matching = _all.Where(r => Include(r) && (term.Length == 0 || Matches(r, term)));

        if (SortColumn is { } column && SortColumns.TryGetValue(column, out var key))
            matching = SortDescending ? matching.OrderByDescending(key) : matching.OrderBy(key);

        List<TRow> matches = [.. matching];

        ListSync.Apply(Items, matches);

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasNoMatches));
        OnPropertyChanged(nameof(IsEmpty));
        OnFiltered();
    }

    /// <summary>Page state that depends on what is on screen. Runs after every filter.</summary>
    protected virtual void OnFiltered()
    {
    }

    /// <summary>Case-insensitive contains, the comparison every one of these pages wants.</summary>
    protected static bool Contains(string? value, string term) =>
        value is not null && value.Contains(term, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Moves a bound collection to a wanted sequence with the fewest changes (KON-164, KON-189).
/// <para>
/// <c>Clear()</c> raises a Reset, and a Reset makes the list throw away every row's visuals — grid,
/// buttons, icons, the lot — and build them again, including for rows that were already on screen
/// and still belong there. Narrowing a search touches a few rows; rebuilding all of them for every
/// keystroke is what the user feels. Add, remove and move only.
/// </para>
/// <para>
/// Separate from <see cref="ListPageViewModel{TRow}"/> because the containers page needs the same
/// reconcile over rows it builds itself: grouped projects fold into the sequence, so its rows are not
/// simply the matching subset of what it loaded.
/// </para>
/// </summary>
public static class ListSync
{
    /// <summary>
    /// Drops the rows that are gone, then walks the wanted sequence and puts the right row at each
    /// index: one already sitting further down is moved up, and only a row that is not there at all is
    /// inserted. Whatever is left past the end goes too.
    /// <para>
    /// The move is what makes a reorder work. Inserting instead left the later copy in place, so one
    /// click on a sortable column header turned three rows into five and then seven, and no amount of
    /// reloading brought them back down (KON-374). The final truncate is the other half: those copies
    /// were all still wanted rows, so the removals at the top left every one of them standing.
    /// </para>
    /// <para>
    /// Quadratic in the worst case (a full reversal scans forward for every row), which lists of a few
    /// hundred rows do not feel. An index would only pay off well past that, and it would have to hold
    /// duplicates, since equal rows are equal by value here.
    /// </para>
    /// </summary>
    public static void Apply<T>(ObservableCollection<T> target, IReadOnlyList<T> desired)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(desired);

        // Drop what is gone first, so narrowing a search stays what it was: removals and nothing else.
        // The loop below would reach the same list by moving rows over the doomed ones instead.
        var keep = new HashSet<T>(desired);
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(target[i]))
                target.RemoveAt(i);
        }

        for (var i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && EqualityComparer<T>.Default.Equals(target[i], desired[i]))
                continue;

            var found = IndexFrom(target, desired[i], i);
            if (found >= 0)
                target.Move(found, i);
            else
                target.Insert(i, desired[i]);
        }

        for (var i = target.Count - 1; i >= desired.Count; i--)
            target.RemoveAt(i);
    }

    /// <summary>First index at or after <paramref name="from"/> holding <paramref name="value"/>, or
    /// -1. Searching from <paramref name="from"/> rather than 0 is what keeps the rows already placed
    /// where they are.</summary>
    private static int IndexFrom<T>(ObservableCollection<T> target, T value, int from)
    {
        for (var i = from; i < target.Count; i++)
        {
            if (EqualityComparer<T>.Default.Equals(target[i], value))
                return i;
        }

        return -1;
    }
}
