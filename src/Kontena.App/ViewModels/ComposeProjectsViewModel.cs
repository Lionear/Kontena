using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Models;
using Kontena.Engines;

namespace Kontena.App.ViewModels;

/// <summary>
/// The Projects page: groups containers into Compose projects by their
/// <c>com.docker.compose.project</c> labels — no compose file parsing or CLI
/// needed — and drives combined start / stop / restart per project.
/// </summary>
public partial class ComposeProjectsViewModel : ViewModelBase, IListPage
{
    public const string ProjectLabel = "com.docker.compose.project";
    public const string ServiceLabel = "com.docker.compose.service";
    public const string ConfigLabel = "com.docker.compose.project.config_files";

    private readonly IContainerEngine _engine;
    private readonly List<ComposeProjectViewModel> _all = [];

    public ComposeProjectsViewModel(IContainerEngine engine)
    {
        _engine = engine;
        SupportsCompose = engine.Capabilities.SupportsCompose;
    }

    /// <summary>Whether the active engine can bring projects up from a compose file.</summary>
    public bool SupportsCompose { get; }

    /// <summary>Opens a service's container in the detail page (set by the shell).</summary>
    public Action<ContainerSummary>? RequestOpenDetail { get; set; }

    /// <summary>Opens the "New Compose project" (up-from-file) modal (set by the shell).</summary>
    public Action? RequestNewProject { get; set; }

    /// <summary>Opens the aggregated-logs modal for a project (set by the shell).</summary>
    public Action<ComposeProjectViewModel>? RequestProjectLogs { get; set; }

    [RelayCommand]
    private void NewProject() => RequestNewProject?.Invoke();

    public void OpenLogs(ComposeProjectViewModel project) => RequestProjectLogs?.Invoke(project);

    public ObservableCollection<ComposeProjectViewModel> Items { get; } = [];

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _hasLoaded;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private bool _isEmpty;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    public async Task LoadAsync()
    {
        var containers = await _engine.ListContainersAsync();

        _all.Clear();
        var projects = containers
            .Where(c => c.Labels.ContainsKey(ProjectLabel))
            .GroupBy(c => c.Labels[ProjectLabel])
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in projects)
            _all.Add(new ComposeProjectViewModel(group.Key, group.ToList(), this));

        HasLoaded = true;
        IsEmpty = _all.Count == 0;
        Summary = _all.Count switch
        {
            0 => "No Compose projects found — start a stack with docker compose or podman-compose.",
            1 => "1 project · grouped from container labels",
            _ => $"{_all.Count} projects · grouped from container labels",
        };

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var filtered = _all.Where(Matches).ToList();

        Items.Clear();
        foreach (var project in filtered)
            Items.Add(project);
    }

    private bool Matches(ComposeProjectViewModel project)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
            return true;

        var q = SearchText.Trim();
        return project.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || project.Services.Any(s =>
                s.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || s.Image.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    // ── Actions shared by project/service rows ────────────────────────────────

    public void OpenDetail(ContainerSummary summary) => RequestOpenDetail?.Invoke(summary);

    public async Task StartProjectAsync(IReadOnlyList<string> ids)
    {
        foreach (var id in ids)
            try { await _engine.StartContainerAsync(id); } catch { /* keep going */ }
        await LoadAsync();
    }

    public async Task StopProjectAsync(IReadOnlyList<string> ids)
    {
        foreach (var id in ids)
            try { await _engine.StopContainerAsync(id); } catch { /* keep going */ }
        await LoadAsync();
    }

    /// <summary>
    /// "Down": stop and remove the project's containers (matching <c>docker compose down</c>),
    /// then best-effort remove its Compose networks (<c>&lt;project&gt;_*</c>). Built from the
    /// container primitives, so it works on every backend without the Compose CLI.
    /// </summary>
    public async Task DownProjectAsync(string project, IReadOnlyList<string> ids)
    {
        foreach (var id in ids)
            try { await _engine.RemoveContainerAsync(id, force: true); } catch { /* keep going */ }

        try
        {
            var networks = await _engine.ListNetworksAsync();
            foreach (var network in networks.Where(n =>
                         !n.IsBuiltIn && n.Name.StartsWith($"{project}_", StringComparison.Ordinal)))
                try { await _engine.RemoveNetworkAsync(network.Id); } catch { /* keep going */ }
        }
        catch { /* network cleanup is best-effort */ }

        await LoadAsync();
    }

    public async Task RestartProjectAsync(IReadOnlyList<string> ids)
    {
        foreach (var id in ids)
            try { await _engine.RestartContainerAsync(id); } catch { /* keep going */ }
        await LoadAsync();
    }

    public async Task StartServiceAsync(string id)
    {
        await _engine.StartContainerAsync(id);
        await LoadAsync();
    }

    public async Task StopServiceAsync(string id)
    {
        await _engine.StopContainerAsync(id);
        await LoadAsync();
    }
}

/// <summary>One Compose project: a named group of service containers.</summary>
public sealed partial class ComposeProjectViewModel : ObservableObject
{
    private readonly ComposeProjectsViewModel _parent;

    public ComposeProjectViewModel(string name, IReadOnlyList<ContainerSummary> containers, ComposeProjectsViewModel parent)
    {
        Name = name;
        _parent = parent;

        ConfigFile = containers
            .Select(c => c.Labels.GetValueOrDefault(ComposeProjectsViewModel.ConfigLabel))
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
            ?.Split(',')[0] ?? string.Empty;

        var ordered = containers
            .OrderBy(c => c.Labels.GetValueOrDefault(ComposeProjectsViewModel.ServiceLabel), StringComparer.OrdinalIgnoreCase)
            .ToList();

        Services = ordered.Select(c => new ComposeServiceViewModel(c, parent)).ToList();

        LogSources = ordered
            .Select(c => new ComposeLogSource(
                c.Labels.GetValueOrDefault(ComposeProjectsViewModel.ServiceLabel, c.Name), c.Id))
            .ToList();

        _ids = containers.Select(c => c.Id).ToList();
        RunningCount = containers.Count(c => c.State == ContainerState.Running);
        TotalCount = containers.Count;
    }

    private readonly List<string> _ids;

    public string Name { get; }
    public string ConfigFile { get; }
    public IReadOnlyList<ComposeServiceViewModel> Services { get; }

    /// <summary>Per-service (name, container-id) pairs for the aggregated-logs view.</summary>
    public IReadOnlyList<ComposeLogSource> LogSources { get; }

    public int RunningCount { get; }
    public int TotalCount { get; }

    public string StatusText => $"{RunningCount} / {TotalCount} running";
    public bool IsAllRunning => RunningCount == TotalCount && TotalCount > 0;
    public bool IsPartial => RunningCount > 0 && RunningCount < TotalCount;
    public bool IsStopped => RunningCount == 0;
    public bool HasStopped => RunningCount < TotalCount;

    public IBrush StatusBrush => new SolidColorBrush(Color.Parse(
        IsAllRunning ? "#34D399" : IsPartial ? "#F5B14C" : "#5C6675"));

    [RelayCommand]
    private Task Start() => _parent.StartProjectAsync(_ids);

    [RelayCommand]
    private Task Restart() => _parent.RestartProjectAsync(_ids);

    [RelayCommand]
    private Task Stop() => _parent.StopProjectAsync(_ids);

    [RelayCommand]
    private Task Down() => _parent.DownProjectAsync(Name, _ids);

    [RelayCommand]
    private void Logs() => _parent.OpenLogs(this);
}

/// <summary>One service within a Compose project (a single container).</summary>
public sealed partial class ComposeServiceViewModel : ObservableObject
{
    private readonly ContainerSummary _c;
    private readonly ComposeProjectsViewModel _parent;

    public ComposeServiceViewModel(ContainerSummary container, ComposeProjectsViewModel parent)
    {
        _c = container;
        _parent = parent;
        Name = container.Labels.GetValueOrDefault(ComposeProjectsViewModel.ServiceLabel, container.Name);
    }

    public string Name { get; }
    public string Image => _c.Image;
    public string StatusText => string.IsNullOrWhiteSpace(_c.Status) ? _c.State.ToString() : _c.Status;

    public string PortsText => _c.Ports.Count == 0
        ? "—"
        : string.Join("  ", _c.Ports.Select(p => $":{p.HostPort}→{p.ContainerPort}"));

    public bool IsRunning => _c.State == ContainerState.Running;
    public bool IsNotRunning => !IsRunning;

    public IBrush StatusBrush => new SolidColorBrush(Color.Parse(_c.State switch
    {
        ContainerState.Running => "#34D399",
        ContainerState.Paused or ContainerState.Restarting => "#F5B14C",
        ContainerState.Exited or ContainerState.Dead => "#F87171",
        _ => "#5C6675",
    }));

    [RelayCommand]
    private void Open() => _parent.OpenDetail(_c);

    [RelayCommand]
    private Task Start() => _parent.StartServiceAsync(_c.Id);

    [RelayCommand]
    private Task Stop() => _parent.StopServiceAsync(_c.Id);
}
