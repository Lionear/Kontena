using System.Collections.ObjectModel;
using System.Threading;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Models;
using Kontena.Engines;

namespace Kontena.App.ViewModels;

/// <summary>The Containers page: lists containers from the active engine and drives actions.</summary>
public partial class ContainersViewModel : ViewModelBase, IListPage, IDisposable
{
    public string SearchPlaceholder => "Search containers…";

    private readonly IContainerEngine _engine;

    public ContainersViewModel(IContainerEngine engine) => _engine = engine;

    /// <summary>Raised when a row is opened; the shell swaps in the detail page.</summary>
    public Action<ContainerSummary>? RequestOpenDetail { get; set; }

    public void OpenDetail(ContainerRowViewModel row) => RequestOpenDetail?.Invoke(row.Summary);

    /// <summary>Raised when Run is clicked; the shell shows the Run modal (optionally pre-filled with an image).</summary>
    public Action<string?>? RequestRunContainer { get; set; }

    [RelayCommand]
    private void RunContainer() => RequestRunContainer?.Invoke(null);

    [RelayCommand]
    private void RunTemplate(string image) => RequestRunContainer?.Invoke(image);

    /// <summary>Raised when the Pull image button is clicked; the shell shows the Pull modal.</summary>
    public Action? RequestPullImage { get; set; }

    [RelayCommand]
    private void PullImage() => RequestPullImage?.Invoke();

    private readonly List<ContainerRowViewModel> _all = [];
    private List<string> _prunableIds = [];

    // Event-driven refresh: engine events (from CLI or any other app) mark the
    // list dirty; a debounce loop reloads ~250ms after the last event.
    private CancellationTokenSource? _watchCts;
    private volatile bool _dirty;
    private long _lastSignalTicks;
    private const int DebounceMs = 250;

    /// <summary>Filtered view bound to the UI — containers and Compose headings in one flat list.</summary>
    public ObservableCollection<ContainerListRowViewModel> Items { get; } = [];

    /// <summary>Group rows, kept across reloads so their expansion survives (KON-159).</summary>
    private readonly Dictionary<string, ComposeGroupRowViewModel> _groups = new(StringComparer.Ordinal);

    [ObservableProperty]
    private string _searchText = string.Empty;

    partial void OnSearchTextChanged(string value)
    {
        // A new query is a new question, so a group the user shut during the previous one is fair game
        // to open again (KON-159).
        foreach (var group in _groups.Values)
            group.ForgetSearchOverride();

        ApplyFilter();
    }

    /// <summary>Reads the stored choice for this backend; the shell owns settings.</summary>
    public Func<bool>? LoadGrouping { get; init; }

    /// <summary>Remembers the choice for this backend.</summary>
    public Action<bool>? SaveGrouping { get; init; }

    /// <summary>Opens the Projects page at this project — the other half of Compose (KON-159).</summary>
    public Action<string>? RequestOpenProject { get; init; }

    public void OpenProject(string project) => RequestOpenProject?.Invoke(project);

    /// <summary>
    /// Whether Compose projects collapse into one row. Default on: the flat list of a stack's
    /// containers is exactly what the report was about.
    /// </summary>
    [ObservableProperty] private bool _isGrouped = true;

    // Redrawing follows every assignment; persisting follows only the user's, which is why the save
    // lives in the command rather than here. Restoring the stored value must not write it back.
    partial void OnIsGroupedChanged(bool value) => ApplyFilter();

    [RelayCommand]
    private void ToggleGrouping()
    {
        IsGrouped = !IsGrouped;
        SaveGrouping?.Invoke(IsGrouped);
    }

    private bool _groupingRestored;

    /// <summary>Redraw the rows after a group opened or closed. Nothing is re-fetched.</summary>
    public void RefreshRows() => ApplyFilter();

    /// <summary>
    /// Reconcile the master list against a fresh snapshot: reuse existing rows
    /// (patched in place), add new ones, drop removed ones. No full rebuild.
    /// </summary>
    private void Reconcile(IReadOnlyList<ContainerSummary> list)
    {
        var byId = _all.ToDictionary(r => r.Id);
        var rebuilt = new List<ContainerRowViewModel>(list.Count);

        foreach (var c in list.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (byId.TryGetValue(c.Id, out var existing))
            {
                existing.Update(c);
                rebuilt.Add(existing);
            }
            else
            {
                rebuilt.Add(new ContainerRowViewModel(c, this));
            }
        }

        _all.Clear();
        _all.AddRange(rebuilt);
    }

    private void ApplyFilter()
    {
        // The same reconcile the other list pages get from ListPageViewModel (KON-189). This page
        // cannot inherit it: with grouping on, its rows are projects and their children, not the
        // matching subset of what it loaded.
        ListSync.Apply(Items, Rows());
        RaiseCollectionState();
    }

    /// <summary>
    /// The rows to show, in one order (KON-159).
    /// <para>
    /// A group sits under its own project name, in the same sequence as the loose containers — not
    /// "groups first, then the rest", which would rearrange the entire list the moment somebody starts
    /// a stack.
    /// </para>
    /// </summary>
    private List<ContainerListRowViewModel> Rows()
    {
        foreach (var row in _all)
            row.IsChild = false;

        if (!IsGrouped)
        {
            PruneGroups([]);
            return [.. _all.Where(Matches)];
        }

        var projects = _all
            .Where(r => r.Project is not null)
            .GroupBy(r => r.Project!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ContainerRowViewModel>)[.. g], StringComparer.Ordinal);

        PruneGroups(projects.Keys);

        var searching = SearchText.Trim() is { Length: > 0 };
        var query = SearchText.Trim();
        var rows = new List<ContainerListRowViewModel>();

        var ordered = projects
            .Select(p => (Head: (ContainerListRowViewModel)GroupFor(p.Key, p.Value), p.Value))
            .Concat(_all.Where(r => r.Project is null)
                .Select(r => (Head: (ContainerListRowViewModel)r, Value: (IReadOnlyList<ContainerRowViewModel>)[])))
            .OrderBy(e => e.Head.SortKey, StringComparer.OrdinalIgnoreCase);

        foreach (var (head, children) in ordered)
        {
            if (head is not ComposeGroupRowViewModel group)
            {
                if (Matches((ContainerRowViewModel)head))
                    rows.Add(head);

                continue;
            }

            // A hit on the project name is a hit on the whole stack: show all of it, rather than
            // making someone wonder which containers the name matched.
            var wholeStack = searching && group.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
            var visible = wholeStack ? children : [.. children.Where(Matches)];

            if (searching && visible.Count == 0)
                continue;

            // A hit inside a collapsed group is a hit nobody sees. Forced open rather than expanded, so
            // clearing the search puts the group back the way the user left it — unless they shut it
            // themselves while this same search was running.
            group.IsForcedOpen = searching && !group.ClosedDuringSearch;
            rows.Add(group);

            if (!group.IsOpen)
                continue;

            foreach (var child in visible)
            {
                child.IsChild = true;
                rows.Add(child);
            }
        }

        return rows;
    }

    /// <summary>One group row per project, reused so expansion outlives a reload.</summary>
    private ComposeGroupRowViewModel GroupFor(string project, IReadOnlyList<ContainerRowViewModel> children)
    {
        if (_groups.TryGetValue(project, out var existing))
        {
            existing.Update(children);
            return existing;
        }

        var group = new ComposeGroupRowViewModel(project, children, this);
        _groups[project] = group;
        return group;
    }

    /// <summary>Forget groups whose project is gone, so a taken-down stack does not linger.</summary>
    private void PruneGroups(IEnumerable<string> alive)
    {
        var keep = new HashSet<string>(alive, StringComparer.Ordinal);
        foreach (var gone in _groups.Keys.Where(k => !keep.Contains(k)).ToList())
            _groups.Remove(gone);
    }

    /// <summary>
    /// How many containers there are, whatever the list is doing with them. The sidebar counts these
    /// and not <see cref="Items"/>: grouping changes how many <i>rows</i> there are, and a nav count
    /// that moved when you folded a project would be counting the wrong noun (KON-159).
    /// </summary>
    public int ContainerCount => _all.Count;

    /// <summary>True once loaded and at least one container exists (drives stat cards + table).</summary>
    public bool HasAnyContainers => HasLoaded && _all.Count > 0;

    /// <summary>Loaded, but no containers at all — show the empty state.</summary>
    public bool IsEmpty => HasLoaded && _all.Count == 0;

    /// <summary>Has containers, but the current search hides them all.</summary>
    public bool HasNoMatches => HasLoaded && _all.Count > 0 && Items.Count == 0;

    private void RaiseCollectionState()
    {
        OnPropertyChanged(nameof(HasAnyContainers));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasNoMatches));
    }

    private bool Matches(ContainerRowViewModel row)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
            return true;

        var q = SearchText.Trim();
        return row.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || row.Image.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    [ObservableProperty]
    private int _runningCount;

    [ObservableProperty]
    private int _stoppedCount;

    [ObservableProperty]
    private string _cpuTotalText = "0%";

    [ObservableProperty]
    private string _memTotalText = "0 MB";

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>True once the first container list has been fetched (drives the loading state).</summary>
    [ObservableProperty]
    private bool _hasLoaded;

    [ObservableProperty] private bool _hasStopped;
    [ObservableProperty] private bool _pruneArmed;
    [ObservableProperty] private string _pruneSummary = string.Empty;

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (IsBusy)
            return;

        if (!_groupingRestored)
        {
            _groupingRestored = true;
            IsGrouped = LoadGrouping?.Invoke() ?? true;
        }

        IsBusy = true;
        try
        {
            var list = await _engine.ListContainersAsync();
            Reconcile(list);

            RunningCount = list.Count(c => c.State == ContainerState.Running);
            StoppedCount = list.Count - RunningCount;

            // Prune only stopped containers we own — never externally-managed ones (e.g. SQL Explorer's).
            _prunableIds = list
                .Where(c => c.State != ContainerState.Running && !c.IsManagedExternally)
                .Select(c => c.Id)
                .ToList();
            var prunable = _prunableIds.Count;
            var managedSkipped = StoppedCount - prunable;

            HasStopped = prunable > 0;
            PruneSummary = $"Remove {prunable} stopped container{(prunable == 1 ? "" : "s")}?"
                + (managedSkipped > 0 ? $"  ({managedSkipped} externally-managed skipped)" : "");
            if (!HasStopped)
                PruneArmed = false;

            HasLoaded = true;
            ApplyFilter(); // show the list immediately — do NOT wait for stats
        }
        finally
        {
            IsBusy = false;
        }

        // Stats are slower (a live sample per container), so fetch them in the
        // background and update rows as they arrive. The list is already visible.
        _ = RefreshStatsAsync();
    }

    private int _statsGeneration;

    private async Task RefreshStatsAsync()
    {
        var generation = ++_statsGeneration;

        var running = _all.Where(r => r.IsRunning).ToList();
        if (running.Count == 0)
        {
            CpuTotalText = "0%";
            MemTotalText = "0 MB";
            return;
        }

        var samples = await Task.WhenAll(running.Select(SampleAsync));

        if (generation != _statsGeneration)
            return; // a newer refresh started while we were sampling

        double cpu = 0;
        long mem = 0;
        foreach (var s in samples)
        {
            if (s is null) continue;
            cpu += s.CpuPercent;
            mem += s.MemoryUsedBytes;
        }

        CpuTotalText = $"{cpu:0}%";
        MemTotalText = $"{mem / 1_000_000} MB";
    }

    private async Task<ContainerStats?> SampleAsync(ContainerRowViewModel row)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            ContainerStats? last = null;
            var count = 0;
            await foreach (var s in _engine.StreamStatsAsync(row.Id, cts.Token))
            {
                last = s;
                row.ApplyStats(s);
                if (++count >= 2) // second sample → accurate CPU% (needs a delta)
                    break;
            }

            return last;
        }
        catch
        {
            // One container's stats failing must never affect the others.
            return null;
        }
    }

    /// <summary>Start reacting to engine events so external changes (CLI, other apps) show up live.</summary>
    public void StartWatching()
    {
        if (_watchCts is not null)
            return;

        _watchCts = new CancellationTokenSource();
        _ = WatchEventsAsync(_watchCts.Token);
        _ = DebounceLoopAsync(_watchCts.Token);
    }

    public void StopWatching()
    {
        _watchCts?.Cancel();
        _watchCts?.Dispose();
        _watchCts = null;
    }

    public void Dispose()
    {
        StopWatching();
        GC.SuppressFinalize(this);
    }

    private async Task WatchEventsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var ev in _engine.StreamEventsAsync(ct))
                {
                    if (ev.ResourceKind == ResourceKind.Container)
                        Signal();
                }

                // Stream ended without error — pause briefly, then re-subscribe.
                await Task.Delay(1000, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Engine hiccup (e.g. restart) — back off, then try to re-subscribe.
                try { await Task.Delay(2000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private void Signal()
    {
        _dirty = true;
        Interlocked.Exchange(ref _lastSignalTicks, DateTime.UtcNow.Ticks);
    }

    private async Task DebounceLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(100, ct);

                if (!_dirty || IsBusy)
                    continue;

                var idleMs = (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastSignalTicks))
                    / TimeSpan.TicksPerMillisecond;
                if (idleMs < DebounceMs)
                    continue; // still receiving events — wait for a quiet window

                _dirty = false;
                await Dispatcher.UIThread.InvokeAsync(LoadAsync);
            }
        }
        catch (OperationCanceledException)
        {
            // watching stopped
        }
    }

    [RelayCommand]
    private void ArmPrune() => PruneArmed = HasStopped;

    [RelayCommand]
    private void CancelPrune() => PruneArmed = false;

    [RelayCommand]
    private async Task PruneAsync()
    {
        PruneArmed = false;

        // Remove only the containers we own; externally-managed ones are left untouched.
        // (A plain engine prune would delete every stopped container, including those.)
        foreach (var id in _prunableIds)
        {
            try { await _engine.RemoveContainerAsync(id, force: false); }
            catch { /* one failure must not stop the rest */ }
        }

        await LoadAsync();
    }

    public async Task StartAsync(string id)
    {
        await _engine.StartContainerAsync(id);
        await LoadAsync();
    }

    public async Task StopAsync(string id)
    {
        await _engine.StopContainerAsync(id);
        await LoadAsync();
    }

    public async Task RestartAsync(string id)
    {
        await _engine.RestartContainerAsync(id);
        await LoadAsync();
    }

    /// <summary>
    /// Ask before removing a container (KON-126). The removal is forced, so a running container is
    /// killed rather than refused — the message has to say that, because the button does not.
    /// </summary>
    public void ConfirmRemove(ContainerRowViewModel row)
    {
        Confirm(
            "Remove container",
            ContainerRemovalMessage(row.Name, row.IsRunning),
            "Remove",
            () => RemoveAsync(row.Id));
    }

    /// <summary>
    /// Shared with the detail page, which removes the same container from its own header — one wording
    /// for one action, wherever it is triggered.
    /// </summary>
    public static string ContainerRemovalMessage(string name, bool isRunning)
        => $"Remove container \"{name}\"?{(isRunning ? " It is running and will be killed first." : "")}"
        + " Anything written inside it that is not on a volume is lost; volumes and the image stay.";

    public async Task RemoveAsync(string id)
    {
        await _engine.RemoveContainerAsync(id, force: true);
        await LoadAsync();
    }

    // ── Compose project actions, from the group row (KON-159) ─────────────────
    //
    // Built on the container primitives rather than the Compose CLI, exactly as the Projects page
    // does — so they work on every backend, and one container refusing does not strand the rest.

    public async Task StartProjectAsync(IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        foreach (var id in ids)
            try { await _engine.StartContainerAsync(id); } catch { /* keep going */ }

        await LoadAsync();
    }

    public async Task StopProjectAsync(IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        foreach (var id in ids)
            try { await _engine.StopContainerAsync(id); } catch { /* keep going */ }

        await LoadAsync();
    }

    public async Task RestartProjectAsync(IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        foreach (var id in ids)
            try { await _engine.RestartContainerAsync(id); } catch { /* keep going */ }

        await LoadAsync();
    }

    /// <summary>
    /// Ask before taking a project down (KON-126) — the widest removal in the app, so it counts what
    /// goes and says what survives. Same wording as the Projects page: one action, one sentence,
    /// wherever it is triggered.
    /// </summary>
    public async Task ConfirmDownAsync(ComposeGroupRowViewModel group)
    {
        ArgumentNullException.ThrowIfNull(group);

        Confirm(
            ComposeProjectsViewModel.ProjectDownTitle(group.Name),
            ComposeProjectsViewModel.ProjectDownMessage,
            "Take down",
            () => DownProjectAsync(group.Name, group.ContainerIds),
            details: ComposeProjectsViewModel.ProjectDownDetails(
                [.. group.Children.Select(c => c.Service ?? c.Name)],
                await ComposeProjectsViewModel.ProjectNetworkNamesAsync(_engine, group.Name)));
    }

    /// <summary>
    /// Stop and remove the project's containers, then its Compose networks — what
    /// <c>docker compose down</c> does. Volumes and images stay, which is why the message says so.
    /// </summary>
    public async Task DownProjectAsync(string project, IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        foreach (var id in ids)
            try { await _engine.RemoveContainerAsync(id, force: true); } catch { /* keep going */ }

        try
        {
            var networks = await _engine.ListNetworksAsync();
            foreach (var network in networks.Where(n => ComposeProjectsViewModel.IsProjectNetwork(n, project)))
                try { await _engine.RemoveNetworkAsync(network.Id); } catch { /* keep going */ }
        }
        catch { /* network cleanup is best-effort */ }

        await LoadAsync();
    }
}
