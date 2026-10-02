using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.App.Services;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.ViewModels;

/// <summary>One object the sweep found, and the click that opens it.</summary>
public sealed partial class SearchHitRow(SearchHit hit, Action<ResourceRef>? onOpen)
{
    public string Name => hit.Name;

    public string Kind => hit.Kind;

    /// <summary>Group and namespace — the two things that tell apart objects with the same name.</summary>
    public string Where => hit.Namespace is { Length: > 0 } ns
        ? $"{hit.Group} · namespace {ns}"
        : hit.Group;

    [RelayCommand]
    private void Open() => onOpen?.Invoke(hit.Reference);
}

/// <summary>
/// Finds an object by name when you do not know its type (KON-458).
/// <para>
/// The page exists because the alternative is guessing: pick a type, go to its list, search, and start
/// over when the guess was wrong. It implements <see cref="IListPage"/> so the shared command-bar box
/// drives it — there is one search box in this app and this is one more thing it does, rather than a
/// second box that looks the same and behaves differently.
/// </para>
/// </summary>
public sealed partial class ClusterFindViewModel : ViewModelBase, IListPage
{
    /// <summary>
    /// How long typing has to stop before the cluster is asked. A sweep is one listing per kind, so
    /// running it per keystroke would put a hundred requests in flight to answer a term the user was
    /// still halfway through typing.
    /// </summary>
    public const int DebounceMs = 400;

    private readonly IClusterEngine _cluster;
    private readonly string? _namespace;

    /// <summary>
    /// The context this page was built on — the UI thread, because that is where pages are built. Kept
    /// rather than reaching for the dispatcher directly so that a run without one (a test) simply has
    /// nothing to marshal to and calls straight through, instead of posting into a queue nobody pumps.
    /// </summary>
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    private CancellationTokenSource? _sweep;

    public ClusterFindViewModel(IClusterEngine cluster, string? @namespace)
    {
        _cluster = cluster;
        _namespace = @namespace;
    }

    /// <summary>What the sweep has turned up so far, in the order the kinds came back.</summary>
    public ObservableCollection<SearchHitRow> Hits { get; } = [];

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private int _searched;
    [ObservableProperty] private int _totalKinds;

    /// <summary>True once a sweep has run to the end, so "nothing found" can be told from "not yet".</summary>
    [ObservableProperty] private bool _hasSwept;

    /// <summary>Opens the object behind a row. The shell owns navigation.</summary>
    public Action<ResourceRef>? RequestOpen { get; set; }

    /// <inheritdoc/>
    public bool HasLoaded => true;

    /// <inheritdoc/>
    public string SearchPlaceholder => "Find an object by name, in any type…";

    /// <summary>
    /// Nothing to load: this page has no contents until something is typed. Present because
    /// <see cref="IListPage"/> asks for it, and because a refresh here should re-run the search rather
    /// than do nothing.
    /// </summary>
    public Task LoadAsync()
    {
        Restart(SearchText);
        return Task.CompletedTask;
    }

    public bool HasHits => Hits.Count > 0;

    /// <summary>Searched to the end and found nothing — different from a box nobody has typed in.</summary>
    public bool NothingFound => HasSwept && !IsSearching && Hits.Count == 0 && SearchText.Trim().Length > 0;

    /// <summary>Nothing typed yet. The page says what it is for rather than showing an empty list.</summary>
    public bool IsIdle => SearchText.Trim().Length == 0;

    /// <summary>
    /// How far along, said out loud. A sweep over a hundred kinds takes long enough that a bare
    /// spinner reads as a hang, and the count is the honest thing to show — the answer is arriving in
    /// pieces, so the page should look like it.
    /// </summary>
    public string Progress => TotalKinds == 0
        ? string.Empty
        : $"Searched {Searched} of {TotalKinds} resource types · {Hits.Count} found";

    partial void OnSearchTextChanged(string value) => Restart(value);

    partial void OnSearchedChanged(int value) => OnPropertyChanged(nameof(Progress));

    partial void OnIsSearchingChanged(bool value) => OnPropertyChanged(nameof(NothingFound));

    private void Restart(string term)
    {
        _sweep?.Cancel();
        _sweep?.Dispose();
        _sweep = null;

        Hits.Clear();
        Searched = 0;
        TotalKinds = 0;
        HasSwept = false;
        IsSearching = false;
        RaiseState();

        if (term.Trim().Length == 0)
            return;

        _sweep = new CancellationTokenSource();
        _ = RunAsync(term.Trim(), _sweep.Token);
    }

    private async Task RunAsync(string term, CancellationToken ct)
    {
        try
        {
            await Task.Delay(DebounceMs, ct);
        }
        catch (OperationCanceledException)
        {
            // Still typing. The sweep this would have started is not wanted.
            return;
        }

        IsSearching = true;
        RaiseState();

        try
        {
            await ClusterObjectSearch.SweepAsync(
                _cluster, term, _namespace,
                hits => OnUiThread(() =>
                {
                    foreach (var hit in hits)
                        Hits.Add(new SearchHitRow(hit, RequestOpen));

                    RaiseState();
                }),
                (done, total) => OnUiThread(() =>
                {
                    // Workers finish in their own order and report their own count, so these arrive
                    // out of order. Progress that can go backwards is worse than none.
                    Searched = Math.Max(Searched, done);
                    TotalKinds = total;
                }),
                ct);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer term, or the page was left.
            return;
        }
        catch (Exception)
        {
            // Discovery itself failed — no kinds, so nothing to sweep. The empty state covers it.
        }

        if (ct.IsCancellationRequested)
            return;

        IsSearching = false;
        HasSwept = true;
        RaiseState();
    }

    /// <summary>
    /// Hits arrive on whichever worker finished a listing, and the bound collection may only be
    /// touched from the thread the page lives on.
    /// </summary>
    private void OnUiThread(Action action)
    {
        if (_ui is null || ReferenceEquals(SynchronizationContext.Current, _ui))
            action();
        else
            _ui.Post(_ => action(), null);
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(HasHits));
        OnPropertyChanged(nameof(NothingFound));
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(Progress));
    }
}
