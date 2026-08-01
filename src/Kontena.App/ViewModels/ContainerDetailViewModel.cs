using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.App.Services;
using Kontena.Sdk.Models;
using Kontena.Sdk;
using Kontena.Core.Diagnostics;
using Kontena.Core.Models;
using Kontena.Core.Orchestration;

namespace Kontena.App.ViewModels;

/// <summary>
/// The container-detail page: header + live-stat strip + tabbed Logs / Stats
/// (Terminal and Inspect are placeholders pending KON-35 / KON-36). Streams logs
/// and stats from the active engine over the CEAL for as long as it is on screen.
/// </summary>
public partial class ContainerDetailViewModel : ViewModelBase, IDisposable, ITerminalHost, IDetachableDetail
{
    private const int MaxLogLines = 2000;

    private readonly IContainerEngine _engine;
    private ContainerSummary _c;
    private CancellationTokenSource? _cts;

    private readonly List<LogLineViewModel> _all = [];

    /// <summary>Whether the container this page describes is known to be gone (KON-308) — removed here,
    /// or removed elsewhere and noticed on a refresh from within this page.</summary>
    [ObservableProperty] private bool _isSourceGone;

    /// <summary>The container id — stable across the list reloads that hand this page a brand new
    /// ContainerSummary record for the same container (KON-308).</summary>
    public string DetailKey => _c.Id;

    public ContainerDetailViewModel(
        IContainerEngine engine, ContainerSummary container, TerminalFont terminalFont)
    {
        _engine = engine;
        _c = container;

        SupportsStats = engine.Capabilities.SupportsStats;
        SupportsExec = engine.Capabilities.SupportsExec;

        TerminalFontFamily = $"{terminalFont.Family}, monospace";
        TerminalFontSize = terminalFont.Size;
        TerminalLigatures = terminalFont.Ligatures;

        Start();
    }

    /// <summary>Terminal font (from settings), consumed by the Terminal tab.</summary>
    /// <summary>The session belongs to this page: it opened it, and it ends with it.</summary>
    public ValueTask ReleaseExecSessionAsync(IExecSession session, bool discard) => session.DisposeAsync();

    /// <summary>The shell this page execs — see the ExecRequest below.</summary>
    public string ShellLabel => "/bin/sh";

    public string TerminalFontFamily { get; }
    public double TerminalFontSize { get; }
    public bool TerminalLigatures { get; }

    // ── Identity / header ─────────────────────────────────────────────────────

    public string Name => _c.Name;
    public string Backend => _c.Backend;
    public BackendChipInfo BackendChip => BackendChips.For(_c.Backend);
    public string ImageText => _c.Image;
    public string ShortId => _c.Id.Length > 12 ? _c.Id[..12] : _c.Id;
    public string CreatedText => Format.Age(_c.CreatedAt);

    public string PortsText => _c.Ports.Count == 0
        ? "—"
        : string.Join("  ", _c.Ports.Select(p => $"{p.HostPort}→{p.ContainerPort}/{p.Protocol}"));

    public bool IsRunning => _c.State == ContainerState.Running;
    public bool IsNotRunning => !IsRunning;

    /// <summary>True when another app (e.g. SQL Explorer) manages this container.</summary>
    public bool IsManaged => _c.IsManagedExternally;
    public string ManagedByText => $"Managed · {Format.ManagedSource(_c.ManagedSource)}";

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

        if (value == "inspect" && !_inspectLoaded && !InspectLoading)
            _ = LoadInspectAsync();
    }

    public bool IsLogsSelected => SelectedTab == "logs";
    public bool IsStatsSelected => SelectedTab == "stats";
    public bool IsTerminalSelected => SelectedTab == "terminal";
    public bool IsInspectSelected => SelectedTab == "inspect";

    [RelayCommand]
    private void SelectTab(string tab) => SelectedTab = tab;

    // ── Inspect ───────────────────────────────────────────────────────────────

    [ObservableProperty] private ContainerInspect? _inspect;
    [ObservableProperty] private bool _inspectLoading;
    private bool _inspectLoaded;

    public bool HasInspect => Inspect is not null;

    public IReadOnlyList<KeyValueItem> EnvironmentItems =>
        Inspect?.EnvironmentVariables.Select(kv => new KeyValueItem(kv.Key, kv.Value)).ToList() ?? [];
    public IReadOnlyList<KeyValueItem> LabelItems =>
        Inspect?.Labels.Select(kv => new KeyValueItem(kv.Key, kv.Value)).ToList() ?? [];
    public IReadOnlyList<InspectMount> Mounts => Inspect?.Mounts ?? [];
    public IReadOnlyList<InspectNetwork> Networks => Inspect?.Networks ?? [];

    /// <summary>Published ports, host-port first. Sourced from the summary (always available).</summary>
    public IReadOnlyList<PortItem> PortItems =>
        _c.Ports
          .OrderBy(p => p.HostPort ?? int.MaxValue)
          .ThenBy(p => p.ContainerPort)
          .Select(p => new PortItem(p))
          .ToList();

    public bool HasEnvironment => EnvironmentItems.Count > 0;
    public bool HasLabels => LabelItems.Count > 0;
    public bool HasMounts => Mounts.Count > 0;
    public bool HasNetworks => Networks.Count > 0;
    public bool HasPorts => PortItems.Count > 0;
    public bool HasCommand => !string.IsNullOrWhiteSpace(Inspect?.Command);

    public string RestartPolicyText => Inspect?.RestartPolicy switch
    {
        RestartPolicy.Always => "always",
        RestartPolicy.OnFailure => "on-failure",
        RestartPolicy.UnlessStopped => "unless-stopped",
        RestartPolicy.No => "no",
        _ => "—",
    };

    public string StartedText => FormatMoment(Inspect?.StartedAt);
    public string FinishedText => FormatMoment(Inspect?.FinishedAt);

    private static string FormatMoment(DateTimeOffset? when) =>
        when is { } value
            ? value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : "—";

    partial void OnInspectChanged(ContainerInspect? value)
    {
        OnPropertyChanged(nameof(HasInspect));
        OnPropertyChanged(nameof(EnvironmentItems));
        OnPropertyChanged(nameof(LabelItems));
        OnPropertyChanged(nameof(Mounts));
        OnPropertyChanged(nameof(Networks));
        OnPropertyChanged(nameof(HasEnvironment));
        OnPropertyChanged(nameof(HasLabels));
        OnPropertyChanged(nameof(HasMounts));
        OnPropertyChanged(nameof(HasNetworks));
        OnPropertyChanged(nameof(HasCommand));
        OnPropertyChanged(nameof(RestartPolicyText));
        OnPropertyChanged(nameof(StartedText));
        OnPropertyChanged(nameof(FinishedText));
    }

    private async Task LoadInspectAsync()
    {
        InspectLoading = true;
        try
        {
            Inspect = await _engine.InspectContainerAsync(_c.Id);
            _inspectLoaded = true;
        }
        catch
        {
            // Leave Inspect null — the tab shows the empty/failed state.
        }
        finally
        {
            InspectLoading = false;
            Diagnose();
        }
    }

    // ── Diagnosis (KON-150) ───────────────────────────────────────────────────

    /// <summary>
    /// Why this container is not running, when the rules recognise the case. Null means no block is
    /// shown at all: an explanation that might be wrong is worse than none.
    /// </summary>
    [ObservableProperty] private Diagnosis? _diagnosis;

    private ContainerStats? _lastStats;

    public bool HasDiagnosis => Diagnosis is not null;
    public string DiagnosisTitle => Diagnosis?.Title ?? string.Empty;
    public string DiagnosisExplanation => Diagnosis?.Explanation ?? string.Empty;
    public IReadOnlyList<string> DiagnosisEvidence => Diagnosis?.Evidence ?? [];
    public string DiagnosisSuggestion => Diagnosis?.Suggestion ?? string.Empty;
    public bool HasDiagnosisSuggestion => Diagnosis?.Suggestion is { Length: > 0 };

    /// <summary>Only the destinations this page has. Anything else leaves the suggestion as text.</summary>
    public string DiagnosisActionLabel => Diagnosis?.Action switch
    {
        DiagnosisAction.Logs or DiagnosisAction.PreviousLogs => "Logs",
        DiagnosisAction.Inspect => "Inspect",
        _ => string.Empty,
    };

    public bool HasDiagnosisAction => DiagnosisActionLabel.Length > 0;

    partial void OnDiagnosisChanged(Diagnosis? value)
    {
        OnPropertyChanged(nameof(HasDiagnosis));
        OnPropertyChanged(nameof(DiagnosisTitle));
        OnPropertyChanged(nameof(DiagnosisExplanation));
        OnPropertyChanged(nameof(DiagnosisEvidence));
        OnPropertyChanged(nameof(DiagnosisSuggestion));
        OnPropertyChanged(nameof(HasDiagnosisSuggestion));
        OnPropertyChanged(nameof(DiagnosisActionLabel));
        OnPropertyChanged(nameof(HasDiagnosisAction));
    }

    private void Diagnose() => Diagnosis = ContainerDiagnosis.Diagnose(_c, Inspect, _lastStats);

    [RelayCommand]
    private void FollowDiagnosis() =>
        SelectedTab = Diagnosis?.Action == DiagnosisAction.Inspect ? "inspect" : "logs";

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
        _lastStats = s;
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

        // Not lazy like the Inspect tab any more: the exit code and the OOM flag live in the same
        // payload, and they are what the diagnosis is read from (KON-150).
        _ = LoadInspectAsync();

        if (SupportsStats && IsRunning)
            _ = StreamStatsAsync(_cts.Token);

        if (_engine.Capabilities.SupportsEvents)
            _ = FollowForGoneAsync(_cts.Token);
    }

    /// <summary>
    /// The container-side counterpart of ClusterObjectDetailViewModel.FollowForGoneAsync (KON-308).
    /// Without it this page only learns about a removal it performed itself, so a container killed
    /// from a terminal, another window or plain <c>docker rm</c> left a detached window silently
    /// stale — the exact failure the detached window exists to avoid.
    /// </summary>
    private async Task FollowForGoneAsync(CancellationToken ct)
    {
        // A stream that ends cleanly is re-subscribed rather than read as "gone" the way the cluster
        // side reads it: this is the engine-wide event stream ActivityLog and the container list
        // already share, and both treat an end as reconnectable — an engine restart is not a removal.
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var ev in _engine.StreamEventsAsync(ct))
                {
                    if (ev is { Type: EngineEventType.Removed, ResourceKind: ResourceKind.Container }
                        && string.Equals(ev.ResourceId, _c.Id, StringComparison.Ordinal))
                    {
                        IsSourceGone = true;
                        return;
                    }
                }

                await Task.Delay(1000, ct);
            }
            catch (OperationCanceledException)
            {
                return; // page closed
            }
            catch
            {
                // Engine hiccup (e.g. restart) — back off, then try to re-subscribe.
                try { await Task.Delay(2000, ct); }
                catch (OperationCanceledException) { return; }
            }
        }
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

    /// <summary>
    /// Removing the container you are looking at also closes the page, so it goes through the same
    /// confirmation and the same wording as the list (KON-126).
    /// </summary>
    [RelayCommand]
    private void Remove()
        => Confirm(
            "Remove container",
            ContainersViewModel.ContainerRemovalMessage(Name, IsRunning),
            "Remove",
            RemoveAsync);

    private async Task RemoveAsync()
    {
        await _engine.RemoveContainerAsync(_c.Id, force: true);
        IsSourceGone = true;
    }

    [RelayCommand]
    private void Shell() => SelectedTab = "terminal";

    /// <summary>True when an interactive shell can be opened right now.</summary>
    public bool CanOpenTerminal => SupportsExec && IsRunning;

    /// <summary>
    /// Open an attached shell session for the terminal. Ownership (and disposal)
    /// passes to the caller — the terminal view drives and tears it down.
    /// </summary>
    public ValueTask<IExecSession> OpenExecSessionAsync(CancellationToken ct) =>
        _engine.StartExecSessionAsync(_c.Id, new ExecRequest
        {
            Command = ["/bin/sh"],
            Tty = true,
        }, ct);

    /// <summary>Re-fetch this container's summary after a lifecycle action and
    /// restart the stats stream if it just came up.</summary>
    private async Task RefreshSelfAsync()
    {
        var wasRunning = IsRunning;

        var list = await _engine.ListContainersAsync();
        var fresh = list.FirstOrDefault(c => c.Id == _c.Id);
        if (fresh is null)
        {
            // Container vanished (e.g. removed elsewhere) — say so; the host decides what that means.
            IsSourceGone = true;
            return;
        }

        _c = fresh;
        RaiseHeaderChanged();

        // State changed — let the Inspect tab refetch (reload now if it's showing).
        _inspectLoaded = false;
        if (IsInspectSelected)
            _ = LoadInspectAsync();

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
        OnPropertyChanged(nameof(CanOpenTerminal));
        OnPropertyChanged(nameof(PortsText));
        OnPropertyChanged(nameof(PortItems));
        OnPropertyChanged(nameof(HasPorts));
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

/// <summary>A single key/value row (environment variable or label) in the Inspect tab.</summary>
public sealed record KeyValueItem(string Key, string Value);

/// <summary>A published port mapping row in the Inspect tab, with a browser affordance.</summary>
public sealed partial class PortItem
{
    private readonly PortBinding _p;

    public PortItem(PortBinding binding) => _p = binding;

    /// <summary>Display text, e.g. <c>8080 → 80/tcp</c> or <c>80/tcp</c> when unpublished.</summary>
    public string Text => _p.HostPort is { } host
        ? $"{host} → {_p.ContainerPort}/{_p.Protocol}"
        : $"{_p.ContainerPort}/{_p.Protocol} (not published)";

    /// <summary>Only published TCP ports can be opened in a browser.</summary>
    public bool IsOpenable =>
        _p.HostPort is not null
        && string.Equals(_p.Protocol, "tcp", StringComparison.OrdinalIgnoreCase);

    /// <summary>The URL to open for a published TCP port; null otherwise.</summary>
    public string? Url => IsOpenable ? $"http://localhost:{_p.HostPort}" : null;

    /// <summary>Open this port in the browser (best-effort). Disabled for non-openable ports.</summary>
    [RelayCommand(CanExecute = nameof(IsOpenable))]
    private void Open()
    {
        if (Url is { } url)
            Browser.OpenUrl(url);
    }
}
