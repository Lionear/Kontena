using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Sdk.Models;
using Kontena.Sdk;
using Kontena.Core.Orchestration;

namespace Kontena.App.ViewModels;

/// <summary>
/// The Projects page: groups containers into Compose projects by their
/// <c>com.docker.compose.project</c> labels — no compose file parsing or CLI
/// needed — and drives combined start / stop / restart per project.
/// </summary>
public partial class ComposeProjectsViewModel : ListPageViewModel<ComposeProjectViewModel>
{
    public override string SearchPlaceholder => "Search projects…";

    public const string ProjectLabel = "com.docker.compose.project";
    public const string ServiceLabel = "com.docker.compose.service";
    public const string ConfigLabel = "com.docker.compose.project.config_files";

    private readonly IContainerEngine _engine;

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

    [ObservableProperty] private string _summary = string.Empty;

    protected override async Task<IReadOnlyList<ComposeProjectViewModel>> LoadRowsAsync(CancellationToken ct)
    {
        var containers = await _engine.ListContainersAsync(ct: ct);

        List<ComposeProjectViewModel> projects = [.. containers
            .Where(c => c.Labels.ContainsKey(ProjectLabel))
            .GroupBy(c => c.Labels[ProjectLabel])
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ComposeProjectViewModel(g.Key, [.. g], this))];

        Summary = projects.Count switch
        {
            0 => "No Compose projects found — start a stack with docker compose or podman-compose.",
            1 => "1 project · grouped from container labels",
            _ => $"{projects.Count} projects · grouped from container labels",
        };

        return projects;
    }

    protected override bool Matches(ComposeProjectViewModel project, string term) =>
        Contains(project.Name, term)
        || project.Services.Any(s => Contains(s.Name, term) || Contains(s.Image, term));

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
    /// Ask before taking a project down (KON-126). The widest removal in the app — every container of
    /// the project at once — so it lists what goes and says what survives (KON-162). The networks are
    /// looked up first: naming them afterwards would be too late to be part of the decision.
    /// </summary>
    public async Task ConfirmDownAsync(ComposeProjectViewModel project)
    {
        ArgumentNullException.ThrowIfNull(project);

        Confirm(
            ProjectDownTitle(project.Name),
            ProjectDownMessage,
            "Take down",
            () => DownProjectAsync(project.Name, project.ContainerIds),
            details: ProjectDownDetails(
                project.Services.Select(s => s.Name).ToList(),
                await ProjectNetworkNamesAsync(_engine, project.Name)));
    }

    // ── The one Down dialog, shared with the group row in the Containers list (KON-159, KON-162) ──
    //
    // One action, one dialog, wherever it is triggered. Split into pieces rather than one string so
    // both callers get the same title, the same sentence and the same inventory.

    public static string ProjectDownTitle(string project) => $"Take down \"{project}\"?";

    /// <summary>
    /// What survives, which is the part a sentence is good at. What goes is a list — see
    /// <see cref="ProjectDownDetails"/>.
    /// </summary>
    public const string ProjectDownMessage =
        "Everything this project owns is stopped and removed. Volumes and images stay, and bringing it" +
        " up again recreates the containers from the same file.";

    /// <summary>
    /// What actually goes, itemised — and only that. Volumes are deliberately absent: this removes
    /// containers and the networks Compose made, exactly as <c>docker compose down</c> does. Listing
    /// them would promise a deletion that does not happen.
    /// </summary>
    public static IReadOnlyList<ConfirmDetail> ProjectDownDetails(
        IReadOnlyList<string> services, IReadOnlyList<string> networks)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(networks);

        var details = new List<ConfirmDetail>
        {
            new("IconContainer", Count(services.Count, "container"), string.Join(", ", services)),
        };

        // No line at all when there are none: "0 networks" is noise in a list meant to be counted.
        if (networks.Count > 0)
            details.Add(new ConfirmDetail("IconNetwork", Count(networks.Count, "network"), string.Join(", ", networks)));

        return details;
    }

    private static string Count(int n, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{n} {noun}{(n == 1 ? "" : "s")}");

    /// <summary>
    /// The networks a Down would remove, asked <em>before</em> confirming so the dialog can name them.
    /// Same predicate the removal uses, so the list and the act cannot drift apart.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ProjectNetworkNamesAsync(
        IContainerEngine engine, string project)
    {
        ArgumentNullException.ThrowIfNull(engine);

        try
        {
            return [.. (await engine.ListNetworksAsync())
                .Where(n => IsProjectNetwork(n, project))
                .Select(n => n.Name)];
        }
        catch
        {
            // The dialog is worth showing without this line; failing the whole confirm over a network
            // list would be the wrong trade.
            return [];
        }
    }

    public static bool IsProjectNetwork(NetworkSummary network, string project)
    {
        ArgumentNullException.ThrowIfNull(network);

        return !network.IsBuiltIn && network.Name.StartsWith($"{project}_", StringComparison.Ordinal);
    }

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

    /// <summary>The project's container ids, for the actions the page runs on its behalf.</summary>
    public IReadOnlyList<string> ContainerIds => _ids;

    [RelayCommand]
    private Task Down() => _parent.ConfirmDownAsync(this);

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
        : string.Join("  ", _c.Ports.Select(p => $"{p.HostPort}→{p.ContainerPort}"));

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
