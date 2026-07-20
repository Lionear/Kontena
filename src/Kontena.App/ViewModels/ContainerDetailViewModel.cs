using System.Collections.ObjectModel;
using System.Threading;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Models;
using Kontena.Engines;

namespace Kontena.App.ViewModels;

/// <summary>
/// The container-detail page: header + live-stat strip + tabbed Logs / Stats
/// (Terminal and Inspect are placeholders pending KON-35 / KON-36). Streams logs
/// and stats from the active engine over the CEAL for as long as it is on screen.
/// </summary>
public partial class ContainerDetailViewModel : ViewModelBase, IDisposable
{
    private const int MaxLogLines = 2000;

    private readonly IContainerEngine _engine;
    private readonly Action _onBack;
    private ContainerSummary _c;
    private CancellationTokenSource? _cts;

    private readonly List<LogLineViewModel> _all = [];

    public ContainerDetailViewModel(IContainerEngine engine, ContainerSummary container, Action onBack)
    {
        _engine = engine;
        _c = container;
        _onBack = onBack;

        SupportsStats = engine.Capabilities.SupportsStats;
        SupportsExec = engine.Capabilities.SupportsExec;

        Start();
    }

    // ── Identity / header ─────────────────────────────────────────────────────

    public string Name => _c.Name;
    public string Backend => _c.Backend;
    public string BackendChip => _c.Backend.Length > 0 ? _c.Backend[..1].ToUpperInvariant() : "?";
    public string ImageText => _c.Image;
    public string ShortId => _c.Id.Length > 12 ? _c.Id[..12] : _c.Id;
    public string CreatedText => Format.Age(_c.CreatedAt);

    public string PortsText => _c.Ports.Count == 0
        ? "—"
        : string.Join("  ", _c.Ports.Select(p => $":{p.HostPort}→{p.ContainerPort}/{p.Protocol}"));

    public bool IsRunning => _c.State == ContainerState.Running;
    public bool IsNotRunning => !IsRunning;

    public string StateText => string.IsNullOrWhiteSpace(_c.Status) ? _c.State.ToString() : _c.Status;
    public string StateLabel => _c.State.ToString();

    public IBrush StatusBrush => new SolidColorBrush(Color.Parse(_c.State switch
    {
        ContainerState.Running => "#34D399",
        ContainerState.Paused or ContainerState.Restarting => "#F5B14C",
        ContainerState.Exited or ContainerState.Dead => "#F87171",
        _ => "#5C6675",
    }));

    // ── Capabilities ──────────────────────────────────────────────────────────

    public bool SupportsStats { get; }
    public bool SupportsExec { get; }

    // ── Tabs ──────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _selectedTab = "logs";

    partial void OnSelectedTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsLogsSelected));
        OnPropertyChanged(nameof(IsStatsSelected));
        OnPropertyChanged(nameof(IsTerminalSelected));
        OnPropertyChanged(nameof(IsInspectSelected));
    }

    public bool IsLogsSelected => SelectedTab == "logs";
    public bool IsStatsSelected => SelectedTab == "stats";
    public bool IsTerminalSelected => SelectedTab == "terminal";
    public bool IsInspectSelected => SelectedTab == "inspect";

    [RelayCommand]
    private void SelectTab(string tab) => SelectedTab = tab;

    // ── Live stats strip ──────────────────────────────────────────────────────

    [ObservableProperty] private string _cpuText = "—";
    [ObservableProperty] private string _memUsedText = "—";
    [ObservableProperty] private string _memLimitText = string.Empty;
    [ObservableProperty] private double _memPercent;
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private string _netIoText = "—";
    [ObservableProperty] private string _blockIoText = "—";

    private void ApplyStats(ContainerStats s)
    {
        CpuText = $"{s.CpuPercent:0.0}%";
        CpuPercent = Math.Clamp(s.CpuPercent, 0, 100);
        MemUsedText = Format.Size(s.MemoryUsedBytes);
        MemLimitText = s.MemoryLimitBytes > 0 ? $"/ {Format.Size(s.MemoryLimitBytes)}" : string.Empty;
        MemPercent = Math.Clamp(s.MemoryFraction * 100, 0, 100);
        NetIoText = $"{Format.Size(s.NetRxBytes)} / {Format.Size(s.NetTxBytes)}";
        BlockIoText = $"{Format.Size(s.BlockReadBytes)} / {Format.Size(s.BlockWriteBytes)}";
    }

    private void ResetStats()
    {
        CpuText = "—";
        CpuPercent = 0;
        MemUsedText = "—";
        MemLimitText = string.Empty;
        MemPercent = 0;
        NetIoText = "—";
        BlockIoText = "—";
    }

    // ── Logs ──────────────────────────────────────────────────────────────────

    /// <summary>Filtered log lines bound to the console.</summary>
    public ObservableCollection<LogLineViewModel> Lines { get; } = [];

    [ObservableProperty] private string _logFilter = string.Empty;
    [ObservableProperty] private bool _autoScroll = true;
    [ObservableProperty] private bool _showTimestamps = true;
    [ObservableProperty] private bool _wrap;

    public TextWrapping LogWrapping => Wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;

    partial void OnWrapChanged(bool value) => OnPropertyChanged(nameof(LogWrapping));

    partial void OnLogFilterChanged(string value)
    {
        Lines.Clear();
        foreach (var line in _all)
        {
            if (Matches(line))
                Lines.Add(line);
        }
    }

    private bool Matches(LogLineViewModel line) =>
        string.IsNullOrWhiteSpace(LogFilter)
        || line.Raw.Contains(LogFilter.Trim(), StringComparison.OrdinalIgnoreCase);

    private void Append(LogLineViewModel line)
    {
        _all.Add(line);

        LogLineViewModel? dropped = null;
        if (_all.Count > MaxLogLines)
        {
            dropped = _all[0];
            _all.RemoveAt(0);
        }

        // The dropped line, if it was visible, is always the oldest visible one.
        if (dropped is not null && Lines.Count > 0 && ReferenceEquals(Lines[0], dropped))
            Lines.RemoveAt(0);

        if (Matches(line))
            Lines.Add(line);
    }

    [RelayCommand]
    private void ToggleFollow() => AutoScroll = !AutoScroll;

    [RelayCommand]
    private void ToggleTimestamps() => ShowTimestamps = !ShowTimestamps;

    [RelayCommand]
    private void ToggleWrap() => Wrap = !Wrap;

    [RelayCommand]
    private void ClearLogs()
    {
        _all.Clear();
        Lines.Clear();
    }

    // ── Streaming lifecycle ───────────────────────────────────────────────────

    private void Start()
    {
        _cts = new CancellationTokenSource();
        _ = StreamLogsAsync(_cts.Token);
        if (SupportsStats && IsRunning)
            _ = StreamStatsAsync(_cts.Token);
    }

    private async Task StreamLogsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var entry in _engine.StreamLogsAsync(_c.Id, follow: true, ct))
                Append(new LogLineViewModel(entry));
        }
        catch (OperationCanceledException)
        {
            // page closed
        }
        catch
        {
            // engine hiccup — logs simply stop; the rest of the page stays usable.
        }
    }

    private async Task StreamStatsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var sample in _engine.StreamStatsAsync(_c.Id, ct))
                ApplyStats(sample);
        }
        catch (OperationCanceledException)
        {
            // page closed
        }
        catch
        {
            // stats unavailable — the strip keeps its last values.
        }
    }

    // ── Header actions ────────────────────────────────────────────────────────

    [RelayCommand]
    private void Back() => _onBack();

    [RelayCommand]
    private async Task RestartAsync()
    {
        await _engine.RestartContainerAsync(_c.Id);
        await RefreshSelfAsync();
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        await _engine.StartContainerAsync(_c.Id);
        await RefreshSelfAsync();
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        await _engine.StopContainerAsync(_c.Id);
        await RefreshSelfAsync();
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        await _engine.RemoveContainerAsync(_c.Id, force: true);
        _onBack();
    }

    [RelayCommand]
    private void Shell() => SelectedTab = "terminal";

    /// <summary>Re-fetch this container's summary after a lifecycle action and
    /// restart the stats stream if it just came up.</summary>
    private async Task RefreshSelfAsync()
    {
        var wasRunning = IsRunning;

        var list = await _engine.ListContainersAsync();
        var fresh = list.FirstOrDefault(c => c.Id == _c.Id);
        if (fresh is null)
        {
            // Container vanished (e.g. removed elsewhere) — leave the detail page.
            _onBack();
            return;
        }

        _c = fresh;
        RaiseHeaderChanged();

        if (!IsRunning)
            ResetStats();

        if (SupportsStats && IsRunning && !wasRunning && _cts is { IsCancellationRequested: false })
            _ = StreamStatsAsync(_cts.Token);
    }

    private void RaiseHeaderChanged()
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsNotRunning));
        OnPropertyChanged(nameof(PortsText));
        OnPropertyChanged(nameof(ImageText));
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        GC.SuppressFinalize(this);
    }
}
