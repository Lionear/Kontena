using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.App.Services;
using Kontena.Core.Models;
using Kontena.Core.Orchestration;
using Kontena.Core.Orchestration.Models;

namespace Kontena.App.ViewModels;

/// <summary>
/// The pod-detail page (KON-70): header + live metrics strip + tabbed Overview / Logs / Shell /
/// Events / YAML. Streams logs (and, when a metrics-server is present, live CPU/memory) from the
/// active cluster over the OAL, and opens an interactive shell reusing the CEAL's
/// <see cref="IExecSession"/>. The container picker chooses which container the logs and shell target.
/// </summary>
public partial class ClusterPodDetailViewModel : ViewModelBase, IDisposable, ITerminalHost
{
    private const int MaxLogLines = 2000;

    private readonly IClusterEngine _cluster;
    private readonly Pod _pod;
    private readonly Action _onBack;
    private readonly Action<Pod>? _onForward;
    private readonly ResourceRef _ref;
    private readonly List<LogLineViewModel> _all = [];

    private CancellationTokenSource? _cts;         // page lifetime (metrics)
    private CancellationTokenSource? _logCts;      // per-container log stream

    public ClusterPodDetailViewModel(
        IClusterEngine cluster, Pod pod, Action onBack, TerminalFont terminalFont, Action<Pod>? onForward = null)
    {
        _cluster = cluster;
        _pod = pod;
        _onBack = onBack;
        _onForward = onForward;
        _ref = new ResourceRef(GroupVersionKind.Pod, pod.Namespace, pod.Name);

        SupportsExec = cluster.Capabilities.Exec;
        SupportsMetrics = cluster.Capabilities.Metrics;
        SupportsPortForward = cluster.Capabilities.PortForward;

        TerminalFontFamily = $"{terminalFont.Family}, monospace";
        TerminalFontSize = terminalFont.Size;
        TerminalLigatures = terminalFont.Ligatures;

        Containers = new ObservableCollection<string>(pod.Containers.Select(c => c.Name));
        _selectedContainer = Containers.FirstOrDefault() ?? "";

        foreach (var c in pod.Containers)
            ContainerRows.Add(new PodContainerRow(c));

        Start();
    }

    // ── Terminal font (ITerminalHost) ─────────────────────────────────────────

    public string TerminalFontFamily { get; }
    public double TerminalFontSize { get; }
    public bool TerminalLigatures { get; }

    // ── Identity / header ─────────────────────────────────────────────────────

    public string Name => _pod.Name;
    public string Namespace => _pod.Namespace;
    public string Node => string.IsNullOrEmpty(_pod.Node) ? "—" : _pod.Node;
    public string Ip => string.IsNullOrEmpty(_pod.Ip) ? "—" : _pod.Ip;
    public string RestartsText => _pod.Restarts.ToString(CultureInfo.InvariantCulture);
    public string QosText => _pod.Qos.ToString();
    public string ControlledBy => string.IsNullOrEmpty(_pod.ControlledBy) ? "—" : _pod.ControlledBy;
    public string AgeText => Format.Duration(_pod.Age);
    public string PhaseText => _pod.Phase.ToString();
    public string ReadyText => $"{_pod.ReadyContainers}/{_pod.Containers.Count}";

    public IBrush StatusBrush => new SolidColorBrush(Color.Parse(_pod.Phase switch
    {
        PodPhase.Running => "#34D399",
        PodPhase.Pending => "#F5B14C",
        PodPhase.Failed => "#F87171",
        PodPhase.Succeeded => "#5B9BD5",
        _ => "#5C6675",
    }));

    public bool IsRunning => _pod.Phase == PodPhase.Running;

    /// <summary>CrashLoopBackOff / not-running hint shown on the header (KON-70 diagnostics).</summary>
    public bool HasDiagnostic => _pod.Containers.Any(c => c.State.Contains("CrashLoop", StringComparison.OrdinalIgnoreCase))
                                 || _pod.Phase is PodPhase.Failed or PodPhase.Pending;

    public string DiagnosticText => _pod.Containers.FirstOrDefault(c =>
        c.State.Contains("CrashLoop", StringComparison.OrdinalIgnoreCase))?.State
        ?? (_pod.Phase == PodPhase.Pending ? "Pod is pending — waiting to be scheduled or pull images." : "Pod is not running.");

    // ── Capabilities ──────────────────────────────────────────────────────────

    public bool SupportsExec { get; }
    public bool SupportsMetrics { get; }
    public bool SupportsPortForward { get; }

    // ── Containers ─────────────────────────────────────────────────────────────

    public ObservableCollection<string> Containers { get; }
    public ObservableCollection<PodContainerRow> ContainerRows { get; } = [];

    [ObservableProperty] private string _selectedContainer;

    partial void OnSelectedContainerChanged(string value) => RestartLogs();

    // ── Metrics strip ──────────────────────────────────────────────────────────

    [ObservableProperty] private string _cpuText = "—";
    [ObservableProperty] private string _memText = "—";

    // ── Tabs ─────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _selectedTab = "overview";

    partial void OnSelectedTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsOverviewSelected));
        OnPropertyChanged(nameof(IsLogsSelected));
        OnPropertyChanged(nameof(IsShellSelected));
        OnPropertyChanged(nameof(IsEventsSelected));
        OnPropertyChanged(nameof(IsYamlSelected));
        OnPropertyChanged(nameof(IsTerminalSelected));

        if (value == "events" && !_eventsLoaded)
            _ = LoadEventsAsync();
        if (value == "yaml" && !_yamlLoaded)
            _ = LoadYamlAsync();
    }

    public bool IsOverviewSelected => SelectedTab == "overview";
    public bool IsLogsSelected => SelectedTab == "logs";
    public bool IsShellSelected => SelectedTab == "shell";
    public bool IsEventsSelected => SelectedTab == "events";
    public bool IsYamlSelected => SelectedTab == "yaml";

    [RelayCommand]
    private void SelectTab(string tab) => SelectedTab = tab;

    // ── Logs ─────────────────────────────────────────────────────────────────

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
            if (Matches(line))
                Lines.Add(line);
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

        if (dropped is not null && Lines.Count > 0 && ReferenceEquals(Lines[0], dropped))
            Lines.RemoveAt(0);

        if (Matches(line))
            Lines.Add(line);
    }

    [RelayCommand] private void ToggleFollow() => AutoScroll = !AutoScroll;
    [RelayCommand] private void ToggleTimestamps() => ShowTimestamps = !ShowTimestamps;
    [RelayCommand] private void ToggleWrap() => Wrap = !Wrap;

    [RelayCommand]
    private void ClearLogs()
    {
        _all.Clear();
        Lines.Clear();
    }

    // ── Events ─────────────────────────────────────────────────────────────────

    public ObservableCollection<PodEventRow> Events { get; } = [];
    [ObservableProperty] private bool _eventsLoading;
    private bool _eventsLoaded;

    public bool HasEvents => Events.Count > 0;

    private async Task LoadEventsAsync()
    {
        EventsLoading = true;
        try
        {
            var events = await _cluster.ListEventsAsync(_pod.Namespace);
            Events.Clear();
            foreach (var e in events.Where(e => e.InvolvedObject.Name == _pod.Name))
                Events.Add(new PodEventRow(e));
            _eventsLoaded = true;
        }
        catch
        {
            // leave empty
        }
        finally
        {
            EventsLoading = false;
            OnPropertyChanged(nameof(HasEvents));
        }
    }

    // ── YAML ────────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _yamlText = string.Empty;
    [ObservableProperty] private bool _yamlLoading;
    private bool _yamlLoaded;

    /// <summary>The manifest as fetched, so edits can be reverted and dirt detected.</summary>
    private string _yamlOriginal = string.Empty;

    /// <summary>Result of the last apply from this tab; cleared as soon as the text changes again.</summary>
    [ObservableProperty] private string? _yamlStatus;
    [ObservableProperty] private bool _yamlStatusIsError;
    [ObservableProperty] private bool _yamlApplying;

    /// <summary>Red for a rejected apply, muted for a successful one.</summary>
    public IBrush YamlStatusBrush =>
        new SolidColorBrush(Color.Parse(YamlStatusIsError ? "#F87171" : "#9AA4B2"));

    partial void OnYamlStatusIsErrorChanged(bool value) => OnPropertyChanged(nameof(YamlStatusBrush));

    /// <summary>Whether the editor differs from the live manifest.</summary>
    public bool YamlIsDirty => _yamlLoaded && !string.Equals(YamlText, _yamlOriginal, StringComparison.Ordinal);

    public bool CanApplyYaml => YamlIsDirty && !YamlApplying;

    partial void OnYamlTextChanged(string value)
    {
        YamlStatus = null;
        OnPropertyChanged(nameof(YamlIsDirty));
        OnPropertyChanged(nameof(CanApplyYaml));
    }

    partial void OnYamlApplyingChanged(bool value) => OnPropertyChanged(nameof(CanApplyYaml));

    private async Task LoadYamlAsync()
    {
        YamlLoading = true;
        try
        {
            _yamlOriginal = await _cluster.GetManifestAsync(_ref);
            YamlText = _yamlOriginal;
            _yamlLoaded = true;
        }
        catch
        {
            YamlText = "# Could not fetch the manifest.";
        }
        finally
        {
            YamlLoading = false;
            OnPropertyChanged(nameof(YamlIsDirty));
            OnPropertyChanged(nameof(CanApplyYaml));
        }
    }

    /// <summary>Discard local edits and go back to the live manifest.</summary>
    [RelayCommand]
    private void RevertYaml()
    {
        YamlText = _yamlOriginal;
        YamlStatus = null;
    }

    /// <summary>
    /// Patch the live resource with the edited manifest (KON-69). Reports the per-resource result
    /// inline and reloads, so the editor always ends up showing what the cluster actually holds.
    /// </summary>
    [RelayCommand]
    private async Task ApplyYamlAsync()
    {
        if (!CanApplyYaml)
            return;

        YamlApplying = true;
        YamlStatus = null;
        try
        {
            var results = new List<ApplyProgress>();
            await foreach (var progress in _cluster.ApplyAsync(new ManifestBundle { Yaml = YamlText, Source = "editor" }))
                results.Add(progress);

            var failed = results.Find(r => r.Action == ApplyAction.Failed);
            YamlStatusIsError = failed is not null;
            YamlStatus = failed is not null
                ? failed.Error ?? "Apply failed."
                : results.TrueForAll(r => r.Action == ApplyAction.Unchanged)
                    ? "No changes — the manifest already matches."
                    : $"Applied · {string.Join(", ", results.Select(r => $"{r.Resource.Kind.Kind}/{r.Resource.Name} {r.Action.ToString().ToLowerInvariant()}"))}";

            if (failed is null)
                await LoadYamlAsync();
        }
        catch (Exception ex)
        {
            YamlStatusIsError = true;
            YamlStatus = ex.Message;
        }
        finally
        {
            YamlApplying = false;
        }
    }

    // ── Streaming lifecycle ────────────────────────────────────────────────────

    private void Start()
    {
        _cts = new CancellationTokenSource();
        RestartLogs();
        if (SupportsMetrics && IsRunning)
            _ = StreamMetricsAsync(_cts.Token);
    }

    private void RestartLogs()
    {
        _logCts?.Cancel();
        _logCts?.Dispose();
        _logCts = new CancellationTokenSource();
        _all.Clear();
        Lines.Clear();
        if (!string.IsNullOrEmpty(SelectedContainer))
            _ = StreamLogsAsync(SelectedContainer, _logCts.Token);
    }

    private async Task StreamLogsAsync(string container, CancellationToken ct)
    {
        try
        {
            await foreach (var entry in _cluster.StreamLogsAsync(_ref, container, follow: true, ct))
                Append(new LogLineViewModel(entry));
        }
        catch (OperationCanceledException) { /* container switched or page closed */ }
        catch { /* stream hiccup — logs stop, page stays usable */ }
    }

    private async Task StreamMetricsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var m in _cluster.StreamMetricsAsync(_ref, ct))
            {
                CpuText = $"{m.CpuMillicores}m";
                MemText = Format.Size(m.MemoryBytes);
            }
        }
        catch (OperationCanceledException) { /* page closed */ }
        catch { /* metrics unavailable — strip keeps its last values */ }
    }

    // ── Terminal (ITerminalHost) ────────────────────────────────────────────────

    [RelayCommand] private void Shell() => SelectedTab = "shell";

    public bool IsTerminalSelected => SelectedTab == "shell";

    public bool CanOpenTerminal => SupportsExec && IsRunning && !string.IsNullOrEmpty(SelectedContainer);

    public ValueTask<IExecSession> OpenExecSessionAsync(CancellationToken ct) =>
        _cluster.StartExecSessionAsync(_ref, SelectedContainer, new ExecRequest
        {
            Command = ["/bin/sh"],
            Tty = true,
        }, ct);

    /// <summary>Whether a port-forward can be started (a running pod on a cluster that supports it).</summary>
    public bool CanPortForward => _onForward is not null && SupportsPortForward && IsRunning;

    [RelayCommand]
    private void PortForward() => _onForward?.Invoke(_pod);

    [RelayCommand]
    private void Back() => _onBack();

    public void Dispose()
    {
        _logCts?.Cancel();
        _logCts?.Dispose();
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _logCts = null;
        GC.SuppressFinalize(this);
    }
}

/// <summary>A container row in the pod-detail Overview tab.</summary>
public sealed class PodContainerRow
{
    public PodContainerRow(ContainerStatus c)
    {
        Name = c.Name;
        Image = c.Image;
        ReadyText = c.Ready ? "Ready" : "Not ready";
        Restarts = c.Restarts.ToString(CultureInfo.InvariantCulture);
        State = c.State;
        StatusBrush = new SolidColorBrush(Color.Parse(c.Ready ? "#34D399"
            : c.State.Contains("CrashLoop", StringComparison.OrdinalIgnoreCase) ? "#F87171" : "#F5B14C"));
    }

    public string Name { get; }
    public string Image { get; }
    public string ReadyText { get; }
    public string Restarts { get; }
    public string State { get; }
    public IBrush StatusBrush { get; }
}

/// <summary>An event row in the pod-detail Events tab.</summary>
public sealed class PodEventRow
{
    public PodEventRow(ClusterEvent e)
    {
        Reason = e.Reason;
        Message = e.Message;
        Source = e.Source;
        CountText = e.Count > 1 ? $"×{e.Count}" : string.Empty;
        Age = Format.Age(e.LastSeen);
        IsWarning = e.Severity == EventSeverity.Warning;
        SeverityBrush = new SolidColorBrush(Color.Parse(IsWarning ? "#F5B14C" : "#5C6675"));
    }

    public string Reason { get; }
    public string Message { get; }
    public string Source { get; }
    public string CountText { get; }
    public string Age { get; }
    public bool IsWarning { get; }
    public IBrush SeverityBrush { get; }
}
