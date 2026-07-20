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

    /// <summary>Filtered view bound to the UI.</summary>
    public ObservableCollection<ContainerRowViewModel> Items { get; } = [];

    [ObservableProperty]
    private string _searchText = string.Empty;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

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
        SyncCollection(Items, _all.Where(Matches).ToList());
        RaiseCollectionState();
    }

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

    /// <summary>Mutate <paramref name="target"/> the minimum needed to match
    /// <paramref name="desired"/> (add/remove/move only), preserving unchanged rows.</summary>
    private static void SyncCollection(
        ObservableCollection<ContainerRowViewModel> target, List<ContainerRowViewModel> desired)
    {
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(target[i]))
                target.RemoveAt(i);
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var want = desired[i];
            if (i >= target.Count)
            {
                target.Add(want);
            }
            else if (!ReferenceEquals(target[i], want))
            {
                var at = target.IndexOf(want);
                if (at >= 0)
                    target.Move(at, i);
                else
                    target.Insert(i, want);
            }
        }
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

    public async Task RemoveAsync(string id)
    {
        await _engine.RemoveContainerAsync(id, force: true);
        await LoadAsync();
    }
}
