using System.Collections.ObjectModel;
using System.Threading;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Models;
using Kontena.Engines;

namespace Kontena.App.ViewModels;

/// <summary>The Containers page: lists containers from the active engine and drives actions.</summary>
public partial class ContainersViewModel : ViewModelBase, IDisposable
{
    private readonly IContainerEngine _engine;

    public ContainersViewModel(IContainerEngine engine) => _engine = engine;

    private readonly List<ContainerRowViewModel> _all = [];

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

    private void ApplyFilter()
    {
        Items.Clear();
        foreach (var row in _all.Where(Matches))
            Items.Add(row);
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

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            var list = await _engine.ListContainersAsync();
            _all.Clear();
            foreach (var c in list)
                _all.Add(new ContainerRowViewModel(c, this));

            RunningCount = list.Count(c => c.State == ContainerState.Running);
            StoppedCount = list.Count - RunningCount;

            double cpu = 0;
            long mem = 0;
            foreach (var row in _all.Where(r => r.IsRunning))
            {
                try
                {
                    using var statsCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await foreach (var s in _engine.StreamStatsAsync(row.Id, statsCts.Token))
                    {
                        row.ApplyStats(s);
                        cpu += s.CpuPercent;
                        mem += s.MemoryUsedBytes;
                        break; // one sample is enough for the overview
                    }
                }
                catch
                {
                    // A single container's stats failing must never sink the whole list.
                }
            }

            CpuTotalText = $"{cpu:0}%";
            MemTotalText = $"{mem / 1_000_000} MB";

            ApplyFilter();
        }
        finally
        {
            IsBusy = false;
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
