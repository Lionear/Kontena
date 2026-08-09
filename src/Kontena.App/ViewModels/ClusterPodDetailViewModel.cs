using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.App.Services;
using Kontena.Sdk.Models;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;
using Kontena.Core.Diagnostics;
using Kontena.Core.Models;
using Kontena.Core.Orchestration;

namespace Kontena.App.ViewModels;

/// <summary>
/// The pod-detail page (KON-70): header + live metrics strip + tabbed Overview / Logs / Shell /
/// Events / YAML. Streams logs (and, when a metrics-server is present, live CPU/memory) from the
/// active cluster over the OAL, and opens an interactive shell reusing the CEAL's
/// <see cref="IExecSession"/>. The container picker chooses which container the logs and shell target.
/// </summary>
public partial class ClusterPodDetailViewModel : ViewModelBase, IDisposable, ITerminalHost, IDetachableDetail
{
    private const int MaxLogLines = 2000;

    private readonly IClusterEngine _cluster;
    private readonly Pod _pod;
    private readonly Action<Pod>? _onForward;
    private readonly PortForwardRegistry? _portForwards;
    private readonly Func<ResourceRef, Task<bool>>? _onOpenController;
    private readonly Action? _onDelete;
    private readonly ResourceRef _ref;
    private readonly List<LogLineViewModel> _all = [];

    /// <summary>Whether this pod is known to be gone (KON-308) — see ClusterObjectDetailViewModel's
    /// FollowForGoneAsync for the same mechanism on the other five kinds.</summary>
    [ObservableProperty] private bool _isSourceGone;

    /// <summary>Pod/name (ns:…) — stable across the list reloads that hand this page a brand new Pod
    /// record for the same pod (KON-308).</summary>
    public string DetailKey => _ref.ToString();

    /// <summary>Whether the shell wired a delete (KON-334).</summary>
    public bool CanDelete => _onDelete is not null;

    [RelayCommand]
    private void Delete() => _onDelete?.Invoke();

    private CancellationTokenSource? _cts;         // page lifetime (metrics, watch)
    private CancellationTokenSource? _logCts;      // per-container log stream

    /// <param name="onDelete">Invoked by the header's Delete (KON-334). The shell's, for the same
    /// reason as on the other detail pages: deleting the pod this page describes also has to close
    /// the page and drop the history step that leads back to it (KON-173).</param>
    public ClusterPodDetailViewModel(
        IClusterEngine cluster, Pod pod, TerminalFont terminalFont, Action<Pod>? onForward = null,
        PortForwardRegistry? portForwards = null, Func<ResourceRef, Task<bool>>? onOpenController = null,
        Action? onDelete = null)
    {
        _cluster = cluster;
        _pod = pod;
        _onForward = onForward;
        _portForwards = portForwards;
        _onOpenController = onOpenController;
        _onDelete = onDelete;
        _ref = new ResourceRef(GroupVersionKind.Pod, pod.Namespace, pod.Name);

        SupportsExec = cluster.Capabilities.Exec;
        SupportsMetrics = cluster.Capabilities.Metrics;
        SupportsPortForward = cluster.Capabilities.PortForward;

        // KON-321: the modal that starts a forward is not the only place someone looks to stop one —
        // the page they started it from is where they go first, and until now it had no idea a forward
        // for this pod even existed.
        if (_portForwards is not null)
            _portForwards.Changed += OnPortForwardsChanged;

        TerminalFontFamily = $"{terminalFont.Family}, monospace";
        TerminalFontSize = terminalFont.Size;
        TerminalLigatures = terminalFont.Ligatures;

        // Every container, init ones first — a pod stuck in Init:CrashLoopBackOff is exactly when you
        // want those logs, and until KON-168 they were not on offer at all.
        var all = pod.AllContainers;
        Containers = new ObservableCollection<string>(all.Select(c => c.Name));
        _containerByName = all.ToDictionary(c => c.Name, StringComparer.Ordinal);

        // Land on whatever is holding the pod up, otherwise on the app container. Ordering the list
        // init-first is right — that is the order they run — but selecting init-first is not: on a
        // healthy pod that opens the logs of something that finished before the pod started.
        _selectedContainer = (all.FirstOrDefault(c => c.Kind == ContainerKind.Init && !c.CompletedSuccessfully)
                              ?? (pod.Containers.Count > 0 ? pod.Containers[0] : null)
                              ?? (all.Count > 0 ? all[0] : null))?.Name ?? "";

        foreach (var c in all)
            ContainerRows.Add(new PodContainerRow(c));

        Start();
    }

    // ── Terminal font (ITerminalHost) ─────────────────────────────────────────

    /// <summary>The session belongs to this page: it opened it, and it ends with it.</summary>
    public ValueTask ReleaseExecSessionAsync(IExecSession session, bool discard) => session.DisposeAsync();

    /// <summary>The shell this page execs — see the ExecRequest below.</summary>
    public string ShellLabel => "/bin/sh";

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

    /// <summary>The owner as a ref, when its kind is one of the pages that exist to open (KON-322).
    /// <see cref="Pod.ControlledBy"/> is a plain "Kind/name" string — the adapter already rolls a
    /// ReplicaSet up to its Deployment, so every kind that reaches here is a workload kind.</summary>
    private ResourceRef? ControllerRef =>
        _pod.ControlledBy.Split('/', 2) is [var kind, var name] && name.Length > 0
        && Enum.TryParse<WorkloadKind>(kind, out var workloadKind)
            ? new ResourceRef(GroupVersionKind.For(workloadKind), _pod.Namespace, name)
            : null;

    public bool CanOpenController => _onOpenController is not null && ControllerRef is not null;

    [RelayCommand]
    private async Task OpenController()
    {
        if (ControllerRef is { } target && _onOpenController is not null)
            await _onOpenController(target);
    }
    public string AgeText => Format.Duration(_pod.Age);
    /// <summary>Reports the init phase while it runs — "Init:1/2" says what "Pending" cannot.</summary>
    public string PhaseText => _pod.StatusText;
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

    // ── Diagnosis (KON-150) ───────────────────────────────────────────────────

    /// <summary>
    /// The explanation of why this pod is not running, when the rules recognise the case. Null is a
    /// deliberate outcome and not a failure to load: an unrecognised pod gets no block at all, because
    /// a wrong explanation sends someone looking in the wrong place.
    /// </summary>
    [ObservableProperty] private Diagnosis? _diagnosis;

    public bool HasDiagnosis => Diagnosis is not null;
    public string DiagnosisTitle => Diagnosis?.Title ?? string.Empty;
    public string DiagnosisExplanation => Diagnosis?.Explanation ?? string.Empty;
    public IReadOnlyList<string> DiagnosisEvidence => Diagnosis?.Evidence ?? [];
    public string DiagnosisSuggestion => Diagnosis?.Suggestion ?? string.Empty;
    public bool HasDiagnosisSuggestion => Diagnosis?.Suggestion is { Length: > 0 };

    /// <summary>The label of the button the suggestion leads to, when this page can carry it out.</summary>
    public string DiagnosisActionLabel => Diagnosis?.Action switch
    {
        DiagnosisAction.PreviousLogs => "Previous logs",
        DiagnosisAction.Logs => "Logs",
        DiagnosisAction.Events => "Events",
        DiagnosisAction.Manifest => "YAML",
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

    /// <summary>Go where the suggestion points. Every destination is a tab this page already has.</summary>
    [RelayCommand]
    private void FollowDiagnosis()
    {
        switch (Diagnosis?.Action)
        {
            case DiagnosisAction.PreviousLogs:
                // Select the container the diagnosis is about first: landing on the previous logs of a
                // container that never crashed is an empty console and a wrong impression.
                if (Diagnosis is { Code: "CrashLoopBackOff" }
                    && _pod.AllContainers.FirstOrDefault(c => c.Reason == "CrashLoopBackOff") is { } crashing)
                    SelectedContainer = crashing.Name;
                ShowPreviousLogs = true;
                SelectedTab = "logs";
                break;
            case DiagnosisAction.Logs:
                SelectedTab = "logs";
                break;
            case DiagnosisAction.Events:
                SelectedTab = "events";
                break;
            case DiagnosisAction.Manifest:
                SelectedTab = "yaml";
                break;
        }
    }

    // ── Capabilities ──────────────────────────────────────────────────────────

    public bool SupportsExec { get; }
    public bool SupportsMetrics { get; }
    public bool SupportsPortForward { get; }

    // ── Containers ─────────────────────────────────────────────────────────────

    public ObservableCollection<string> Containers { get; }
    public ObservableCollection<PodContainerRow> ContainerRows { get; } = [];

    private readonly Dictionary<string, ContainerStatus> _containerByName;

    [ObservableProperty] private string _selectedContainer;

    private ContainerStatus? Selected =>
        _containerByName.TryGetValue(SelectedContainer ?? string.Empty, out var c) ? c : null;

    /// <summary>
    /// Why the shell is unavailable, when it is. A finished init container is the common case, and it
    /// is not a failure — it is what success looks like for one. Saying so beats a disabled button
    /// with no explanation, and beats a shell that opens onto nothing.
    /// </summary>
    public string? ExecBlockedReason => Selected switch
    {
        null => null,
        { CanExec: true } => null,
        { Kind: ContainerKind.Init, CompletedSuccessfully: true } => "This init container finished before the pod started — there is nothing left to attach to.",
        { RunState: ContainerRunState.Terminated } => "This container has exited.",
        { RunState: ContainerRunState.Waiting } => "This container is not running yet.",
        _ => "This container is not running.",
    };

    public bool HasExecBlockedReason => ExecBlockedReason is not null;

    partial void OnSelectedContainerChanged(string value)
    {
        // Carrying the previous-run toggle to a container that never restarted shows an empty console,
        // which reads as "it logged nothing" rather than "there is no earlier run to show".
        if (!HasPreviousRun)
            _showPreviousLogs = false;

        RestartLogs();
        OnPropertyChanged(nameof(ShowPreviousLogs));
        OnPropertyChanged(nameof(HasPreviousRun));
        OnPropertyChanged(nameof(ExecBlockedReason));
        OnPropertyChanged(nameof(HasExecBlockedReason));
        OnPropertyChanged(nameof(CanOpenTerminal));
    }

    // ── Metrics strip ──────────────────────────────────────────────────────────

    [ObservableProperty] private string _cpuText = "—";
    [ObservableProperty] private string _memText = "—";

    // ── Usage graphs (KON-345) ─────────────────────────────────────────────────

    private readonly UsageSeries _cpuSeries = new(UsageGraphs.LiveBuffer);
    private readonly UsageSeries _memSeries = new(UsageGraphs.LiveBuffer);

    /// <summary>
    /// Both the header sparkline and the Metrics tab, or neither. They are not alternatives: the
    /// sparkline answers "is it steady", the tab answers "what happened at 14:02". Only a cluster
    /// with no metrics source has neither.
    /// </summary>
    public bool ShowUsageGraphs => SupportsMetrics;

    [ObservableProperty] private IReadOnlyList<double> _cpuSamples = [];
    [ObservableProperty] private IReadOnlyList<double> _memSamples = [];

    /// <summary>How far back the charts show, in minutes. Shared by the tab and the sparkline, so
    /// narrowing the range in one narrows the story both of them tell.</summary>
    [ObservableProperty] private int _rangeMinutes = UsageGraphs.DefaultRangeMinutes;

    partial void OnRangeMinutesChanged(int value)
    {
        OnPropertyChanged(nameof(RangeLabel));
        OnPropertyChanged(nameof(RangeOptions));
        RefreshUsage();
    }

    /// <summary>
    /// How far back the chart actually reaches — which is not the selected range until the buffer
    /// has filled. A page open for thirty seconds holds thirty seconds of samples, and an axis
    /// labelled "15m ago" over them claims fourteen and a half minutes that were never sampled.
    /// </summary>
    // ponytail: points are still spaced evenly rather than by timestamp, so a missed scrape shows
    // as a slightly wider straight segment instead of a gap. Fine at a steady 15s poll; revisit if
    // a source with irregular intervals lands (KON-84).
    public string RangeLabel
    {
        get
        {
            var selected = UsageGraphs.Range(RangeMinutes);
            if (_cpuSeries.Oldest is not { } oldest || CpuSamples.Count < 2)
                return Format.Duration(selected);

            var drawn = DateTimeOffset.UtcNow - oldest;
            return Format.Duration(drawn < selected ? drawn : selected);
        }
    }

    /// <summary>
    /// Every range the selector offers, including the ones that need a history source. Shown and
    /// disabled rather than hidden: "why can I not see yesterday" is the question the greyed-out
    /// buttons and their tooltip answer, and a selector that stops at 15m does not raise it.
    /// </summary>
    public IReadOnlyList<UsageRangeOption> RangeOptions =>
        [.. UsageGraphs.Ranges.Select(m => new UsageRangeOption(
            m, Format.Duration(TimeSpan.FromMinutes(m)), UsageGraphs.IsLive(m), m == RangeMinutes))];

    [RelayCommand]
    private void SelectRange(int minutes)
    {
        if (UsageGraphs.IsLive(minutes))
            RangeMinutes = minutes;
    }

    /// <summary>
    /// Where the history comes from, said plainly. Only metrics-server exists today, so this is
    /// also the explanation for the disabled ranges — see KON-84 for the source that removes them.
    /// </summary>
    private string MetricsSourceName =>
        _cluster is IMetricsAware aware ? aware.Metrics.Name : "the metrics source";

    public string UsageSourceText => $"{MetricsSourceName} · sampled every 15s";

    public string UsageRangeHint =>
        $"Charted from what Kontena sampled since this pod was opened — {MetricsSourceName} keeps no "
        + "history. Longer ranges need a history source such as Prometheus.";

    [ObservableProperty] private string _cpuSubText = "millicores";
    [ObservableProperty] private string _memSubText = "working set";

    /// <summary>Nothing has been sampled yet — one poll interval of empty charts otherwise.</summary>
    public bool UsageIsEmpty => CpuSamples.Count == 0;

    private void RefreshUsage()
    {
        var range = UsageGraphs.Range(RangeMinutes);
        var now = DateTimeOffset.UtcNow;

        CpuSamples = _cpuSeries.Window(range, now);
        MemSamples = _memSeries.Window(range, now);

        CpuSubText = Describe(CpuSamples, "millicores", v => $"{Math.Round(v):0}m");
        MemSubText = Describe(MemSamples, "working set", v => ByteSize.Format((long)Math.Round(v)));

        OnPropertyChanged(nameof(UsageIsEmpty));

        // Grows with every sample until the buffer is full, so it cannot be raised on range changes
        // alone — that was the bug: the axis kept saying "15m" over thirty seconds of data.
        OnPropertyChanged(nameof(RangeLabel));
    }

    private static string Describe(
        IReadOnlyList<double> samples, string unit, Func<double, string> format) =>
        samples.Count == 0
            ? unit
            : $"{unit} · peak {format(samples.Max())} · avg {format(samples.Average())}";

    // ── Tabs ─────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _selectedTab = "overview";

    partial void OnSelectedTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsOverviewSelected));
        OnPropertyChanged(nameof(IsLogsSelected));
        OnPropertyChanged(nameof(IsShellSelected));
        OnPropertyChanged(nameof(IsEventsSelected));
        OnPropertyChanged(nameof(IsMetricsSelected));
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
    public bool IsMetricsSelected => SelectedTab == "metrics";
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

    /// <summary>
    /// Show the logs of the run that ended instead of the one running. Only worth offering where there
    /// was an earlier run — on a container that never restarted the previous log is empty, and an empty
    /// console reads as "nothing was logged" rather than "there is no previous run".
    /// </summary>
    [ObservableProperty] private bool _showPreviousLogs;

    public bool HasPreviousRun => Selected is { } c && (c.Restarts > 0 || c.LastExitCode is not null);

    partial void OnShowPreviousLogsChanged(bool value) => RestartLogs();

    [RelayCommand] private void TogglePreviousLogs() => ShowPreviousLogs = !ShowPreviousLogs;

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
            var mine = events.Where(e => e.InvolvedObject.Name == _pod.Name).ToList();

            Events.Clear();
            foreach (var e in mine)
                Events.Add(new PodEventRow(e));
            _eventsLoaded = true;

            // The events are half of the diagnosis, so it is built here rather than in the constructor:
            // the reason code says a pull failed, the event says the registry refused the credentials.
            Diagnosis = PodDiagnosis.Diagnose(_pod, mine);
        }
        catch
        {
            // A cluster that will not hand over its events still has a pod status worth reading.
            Diagnosis = PodDiagnosis.Diagnose(_pod);
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

        // Not lazy like the tab: the diagnosis is the first thing to read on a pod that is stuck, and
        // it needs the events. The Events tab reuses whatever this fetches.
        _ = LoadEventsAsync();

        if (SupportsMetrics && IsRunning)
            _ = StreamMetricsAsync(_cts.Token);

        if (_cluster.Capabilities.Watch)
            _ = FollowForGoneAsync(_cts.Token);
    }

    /// <summary>Same mechanism as ClusterObjectDetailViewModel's FollowForGoneAsync (KON-308): a
    /// Deleted event for this pod on the watch, or that watch ending on its own.</summary>
    private async Task FollowForGoneAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var e in _cluster.WatchAsync(_ref.Kind, _ref.Namespace, ct))
            {
                if (e.Resource.Name != _ref.Name)
                    continue;

                if (e.Type == WatchEventType.Deleted)
                {
                    IsSourceGone = true;
                    return;
                }
            }

            if (!ct.IsCancellationRequested)
                IsSourceGone = true;
        }
        catch (OperationCanceledException) { /* page closed */ }
        catch (Exception)
        {
            if (!ct.IsCancellationRequested)
                IsSourceGone = true;
        }
    }

    private void RestartLogs()
    {
        _logCts?.Cancel();
        _logCts?.Dispose();
        _logCts = new CancellationTokenSource();
        _all.Clear();
        Lines.Clear();
        if (!string.IsNullOrEmpty(SelectedContainer))
            _ = StreamLogsAsync(SelectedContainer, ShowPreviousLogs, _logCts.Token);
    }

    private async Task StreamLogsAsync(string container, bool previous, CancellationToken ct)
    {
        try
        {
            await foreach (var entry in _cluster.StreamLogsAsync(_ref, container, follow: !previous, previous, ct))
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

                // Fall back to arrival time: a source that leaves Timestamp unset would otherwise
                // hand every sample the same instant, and UsageSeries drops those as duplicates.
                var at = m.Timestamp == default ? DateTimeOffset.UtcNow : m.Timestamp;
                _cpuSeries.Add(at, m.CpuMillicores);
                _memSeries.Add(at, m.MemoryBytes);
                RefreshUsage();
            }
        }
        catch (OperationCanceledException) { /* page closed */ }
        catch { /* metrics unavailable — strip keeps its last values */ }
    }

    // ── Terminal (ITerminalHost) ────────────────────────────────────────────────

    [RelayCommand] private void Shell() => SelectedTab = "shell";

    public bool IsTerminalSelected => SelectedTab == "shell";

    // Gated on the selected container rather than the pod: a pod can be "running" while the container
    // you picked has already exited, and exec there fails deep in the API instead of here.
    public bool CanOpenTerminal => SupportsExec && !string.IsNullOrEmpty(SelectedContainer) && Selected?.CanExec == true;

    public ValueTask<IExecSession> OpenExecSessionAsync(CancellationToken ct) =>
        _cluster.StartExecSessionAsync(_ref, SelectedContainer, new ExecRequest
        {
            Command = ["/bin/sh"],
            Tty = true,
        }, ct);

    /// <summary>Whether a port-forward can be started (a running pod on a cluster that supports it).
    /// False while one is already active — starting a second one for the same pod is what
    /// <see cref="StopForward"/> is for.</summary>
    public bool CanPortForward => _onForward is not null && SupportsPortForward && IsRunning && ActiveForward is null;

    [RelayCommand]
    private void PortForward() => _onForward?.Invoke(_pod);

    /// <summary>The forward this page started, if it is still running (KON-321).</summary>
    public ActivePortForward? ActiveForward =>
        _portForwards?.Forwards.FirstOrDefault(f => f.Target == _ref && f.IsActive);

    public bool CanStopForward => ActiveForward is not null;

    [RelayCommand]
    private async Task StopForward()
    {
        if (ActiveForward is { } forward && _portForwards is not null)
            await _portForwards.StopAsync(forward);
    }

    private void OnPortForwardsChanged()
    {
        OnPropertyChanged(nameof(ActiveForward));
        OnPropertyChanged(nameof(CanStopForward));
        OnPropertyChanged(nameof(CanPortForward));
    }

    public void Dispose()
    {
        if (_portForwards is not null)
            _portForwards.Changed -= OnPortForwardsChanged;

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
        Restarts = c.Restarts.ToString(CultureInfo.InvariantCulture);
        State = c.State;
        IsInit = c.Kind == ContainerKind.Init;
        KindLabel = c.Kind switch
        {
            ContainerKind.Init => "init",
            ContainerKind.Ephemeral => "debug",
            _ => string.Empty,
        };
        HasKindLabel = KindLabel.Length > 0;

        // The column is too narrow for four ports, so the row trims and the full list has to live
        // somewhere. One list, two renderings: the cell joins it, the tooltip stacks it (KON-199).
        var ports = c.Ports.Select(p =>
            p.Name.Length > 0
                ? $"{p.Number}/{p.Protocol} {p.Name}"
                : $"{p.Number}/{p.Protocol}").ToList();
        PortsText = ports.Count == 0 ? "—" : string.Join(", ", ports);
        // Null, not empty: a tooltip on a cell that reads "—" would open to say nothing.
        PortsTooltip = ports.Count == 0 ? null : string.Join("\n", ports);

        // "Ready" is the wrong word for an init container: it is supposed to finish, and a finished one
        // reading "Not ready" describes success as a fault.
        ReadyText = IsInit
            ? c.CompletedSuccessfully ? "Completed" : "Not completed"
            : c.Ready ? "Ready" : "Not ready";

        var healthy = IsInit ? c.CompletedSuccessfully : c.Ready;
        StatusBrush = new SolidColorBrush(Color.Parse(healthy ? "#34D399"
            : c.State.Contains("CrashLoop", StringComparison.OrdinalIgnoreCase) ? "#F87171" : "#F5B14C"));
    }

    public string Name { get; }
    public string Image { get; }
    public string ReadyText { get; }
    public string Restarts { get; }
    public string State { get; }
    public string PortsText { get; }
    public string? PortsTooltip { get; }
    public bool IsInit { get; }
    public string KindLabel { get; }
    public bool HasKindLabel { get; }
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

/// <summary>
/// One button in the usage-graph range selector (KON-345). <paramref name="IsAvailable"/> is false
/// for the ranges only a history source can answer; those stay on screen, disabled.
/// </summary>
public sealed record UsageRangeOption(int Minutes, string Label, bool IsAvailable, bool IsSelected);
