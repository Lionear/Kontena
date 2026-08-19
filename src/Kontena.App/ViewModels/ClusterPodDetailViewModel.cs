using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.App.Controls;
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
        Action? onDelete = null, string initialTab = "overview")
    {
        _cluster = cluster;
        _pod = pod;
        _onForward = onForward;
        _portForwards = portForwards;
        _onOpenController = onOpenController;
        _onDelete = onDelete;
        _ref = new ResourceRef(GroupVersionKind.Pod, pod.Namespace, pod.Name);
        _selectedTab = initialTab;

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

        // The image, on the tab you land on (KON-390). It was only in the container table, which for
        // the one-container pod that most pods are meant scanning a table to read a single fact.
        // Init images stay out: they are not what the pod runs.
        Images = pod.Containers.Count == 1
            ? [pod.Containers[0].Image]
            : [.. pod.Containers.Select(c => $"{c.Name}: {c.Image}")];

        Labels = [.. pod.Labels.OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => $"{l.Key}={l.Value}")];

        // One row per object, not per use: the same secret read by two containers is one secret
        // (KON-390). Read off the pod that is already here — ConfigUses came with the listing.
        ConfigRows =
        [
            .. pod.ConfigUses
                .GroupBy(u => (u.Kind, u.Name))
                .OrderBy(g => g.Key.Kind.Kind, StringComparer.Ordinal)
                .ThenBy(g => g.Key.Name, StringComparer.Ordinal)
                .Select(g => new PodConfigRow(
                    new ResourceRef(g.Key.Kind, pod.Namespace, g.Key.Name), [.. g], cluster.GetConfigDataAsync)),
        ];

        // The declared ceiling, when every container has one. A partial sum would be a limit the
        // pod does not actually have, so a single unlimited container means no line at all.
        var limits = all.Select(c => c.MemoryLimitBytes).ToList();
        double? memoryLimit = limits.Count > 0 && limits.All(l => l is > 0)
            ? limits.Sum(l => l!.Value)
            : null;

        Usage = new UsageTrackViewModel(
            [
                new UsageChartSpec("CPU", UsageChartUnit.Millicores, "Primary", UsageMetric.Cpu, "millicores"),
                new UsageChartSpec("Memory", UsageChartUnit.Bytes, "Accent", UsageMetric.Memory, "working set",
                    memoryLimit,
                    memoryLimit is { } cap ? $"limit {ByteSize.Format((long)cap)}" : null),
            ],
            UsageTarget.Pod(pod.Namespace, pod.Name),
            cluster is IMetricsHistoryAware historyAware ? historyAware.History : null,
            cluster is IMetricsAware metricsAware ? metricsAware.Metrics.Name : "the metrics source");

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

    /// <summary>What is wrong with this pod, or null when nothing is — the same mark the pods list
    /// carries, so a row you opened because it stood out still stands out here (KON-415).</summary>
    public string? Trouble => WorkloadTrouble.DescribePod(_pod);

    public bool HasTrouble => Trouble is not null;

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

    // ── Overview facts (KON-390) ───────────────────────────────────────────────

    /// <summary>The image each app container runs — bare when there is one, prefixed with the
    /// container name when there are several and the bare image would be ambiguous.</summary>
    public IReadOnlyList<string> Images { get; }

    public bool HasImages => Images.Count > 0;
    public string ImagesLabel => Images.Count > 1 ? "IMAGES" : "IMAGE";

    /// <summary>The pod's labels, which are what every Service selector in the namespace matches
    /// against — the fact that answers "why is (or isn't) this pod behind that service".</summary>
    public IReadOnlyList<string> Labels { get; }

    public bool HasLabels => Labels.Count > 0;

    /// <summary>The ConfigMaps and Secrets this pod reads, one row per object (KON-390).</summary>
    public IReadOnlyList<PodConfigRow> ConfigRows { get; }

    public bool HasConfigRows => ConfigRows.Count > 0;

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

    /// <summary>
    /// Both the header sparklines and the Metrics tab, or neither. They are not alternatives: the
    /// sparkline answers "is it steady", the tab answers "what happened at 14:02". Only a cluster
    /// with no metrics source has neither.
    /// </summary>
    public bool ShowUsageGraphs => SupportsMetrics;

    public UsageTrackViewModel Usage { get; }

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
            Services.Diag.Action("apply manifest", "pod editor");

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
        {
            _ = StreamMetricsAsync(_cts.Token);

            // Lazily, and never blocking the page: a cluster with no Prometheus should cost nothing
            // more than one refused request, and the ranges light up if and when it answers.
            _ = Usage.ProbeAsync(_cts.Token);
        }

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
                Usage.Add(at, m.CpuMillicores, m.MemoryBytes);
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

/// <summary>
/// One ConfigMap or Secret a pod reads, on the pod's Overview tab (KON-390) — the object, how this
/// pod reaches it, and its keys once asked for.
/// </summary>
public sealed partial class PodConfigRow : ObservableObject
{
    private readonly Func<ResourceRef, CancellationToken, ValueTask<IReadOnlyList<ConfigEntry>>> _fetch;

    public PodConfigRow(
        ResourceRef reference, IReadOnlyList<ConfigUse> uses,
        Func<ResourceRef, CancellationToken, ValueTask<IReadOnlyList<ConfigEntry>>> fetch)
    {
        ArgumentNullException.ThrowIfNull(uses);

        Reference = reference;
        _fetch = fetch;

        Name = reference.Name;
        IsSecret = reference.Kind == GroupVersionKind.Secret;
        KindLabel = IsSecret ? "Secret" : "ConfigMap";

        // Deduplicated: "read as environment by api" twice is one sentence, and the same object read
        // by two containers is two clauses of one.
        UsageText = string.Join(" · ", uses.Select(Describe).Distinct(StringComparer.Ordinal));
    }

    private static string Describe(ConfigUse use) => use.How switch
    {
        ConfigUseKind.Volume => "mounted as a volume",
        ConfigUseKind.EnvironmentVariable when use.Container.Length > 0 => $"read as environment by {use.Container}",
        ConfigUseKind.EnvironmentVariable => "read as environment",
        ConfigUseKind.EnvironmentFrom when use.Container.Length > 0 => $"read whole as environment by {use.Container}",
        ConfigUseKind.EnvironmentFrom => "read whole as environment",
        _ => "used to pull images",
    };

    public ResourceRef Reference { get; }
    public string Name { get; }
    public string KindLabel { get; }
    public bool IsSecret { get; }
    public string UsageText { get; }

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isBusy;

    /// <summary>What went wrong asking for the keys; usually RBAC saying no.</summary>
    [ObservableProperty] private string? _error;

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>The keys, once the row is open. Empty again the moment it closes — so does any value
    /// that was on screen.</summary>
    [ObservableProperty] private IReadOnlyList<ConfigKeyRow> _keys = [];

    partial void OnKeysChanged(IReadOnlyList<ConfigKeyRow> value) => OnPropertyChanged(nameof(HasKeys));

    public bool HasKeys => Keys.Count > 0;

    /// <summary>Set when the object is open and holds nothing — which is valid, and better said than
    /// shown as a row that unfolds onto nothing.</summary>
    [ObservableProperty] private bool _isEmpty;

    [RelayCommand]
    private async Task Toggle()
    {
        if (IsExpanded)
        {
            // Closing drops the rows, and with them any value that was revealed — same rule the key
            // rows keep for themselves: hidden means gone, not folded away.
            Keys = [];
            IsExpanded = false;
            IsEmpty = false;
            Error = null;
            return;
        }

        IsBusy = true;
        Error = null;

        try
        {
            // The keys come from the values call because that is the only thing that knows them here:
            // a pod carries the names of the objects it reads, never their contents. The values it
            // returns are dropped on the spot — what survives is the name and size of each key, which
            // is what the listing pages carry too. Asking the namespace-wide listers instead would
            // pull every secret in the namespace over the wire to learn about this one.
            var entries = await _fetch(Reference, CancellationToken.None);

            // Reuses the config page's rows whole, reveal behaviour included: a secret's value is
            // fetched when the eye is pressed and dropped again when it is pressed a second time.
            Keys = new ConfigObjectRow(
                Reference, type: null,
                [.. entries.Select(e => new ConfigKey(e.Key, e.SizeBytes))],
                TimeSpan.Zero, _fetch, IsSecret).BuildKeyRows();

            IsEmpty = Keys.Count == 0;
            IsExpanded = true;
        }
        catch (Exception failure)
        {
            // Reading a secret is its own RBAC verb: a pod page that may name the secret is not
            // automatically allowed to open it.
            Error = failure.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
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
