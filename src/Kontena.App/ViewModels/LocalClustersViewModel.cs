using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Adapters.Kubernetes;
using Kontena.Adapters.LocalClusters;
using Kontena.Core.Orchestration.Provisioning;
using Kontena.Core.Tooling;

namespace Kontena.App.ViewModels;

/// <summary>Which part of the page is on screen (KON-76).</summary>
public enum LocalClustersStage
{
    /// <summary>The clusters that exist, and the way to make another.</summary>
    List,

    /// <summary>The create form.</summary>
    Form,

    /// <summary>Something is running — a create or a start — with the tool's own output on screen.</summary>
    Running,

    /// <summary>It failed, and the output is still there to read.</summary>
    Failed,
}

/// <summary>
/// Settings › Local clusters — the clusters kind and minikube own on this machine, and making another
/// (KON-76, KON-77).
/// <para>
/// The tooling page from KON-109 lives on inside this one as <see cref="Tooling"/>: once a tool is
/// present it folds down to a line, because "which binaries are installed" stops being the subject
/// the moment it is settled. Creating a cluster is what the page is for.
/// </para>
/// </summary>
public sealed partial class LocalClustersViewModel : ViewModelBase, IDisposable
{
    private readonly IReadOnlyList<IClusterProvisioner> _provisioners;
    private readonly ToolReadinessCheck _check;

    /// <summary>What each provisioner is for, in the one line the form shows under its name.</summary>
    private static readonly Dictionary<string, string> Purposes = new(StringComparer.Ordinal)
    {
        [KindClusterProvisioner.Id] =
            "Each node is a container on the engine you already have. Fastest to create and throw away.",
        [MinikubeClusterProvisioner.Id] =
            "A VM or container per cluster, with drivers and resource limits — and it can be stopped and started again.",
    };

    private CancellationTokenSource? _running;
    private IReadOnlyList<LocalClusterRuntime> _availableRuntimes = [LocalClusterRuntime.Docker];

    public LocalClustersViewModel(
        IReadOnlyList<IClusterProvisioner>? provisioners = null,
        IToolRunner? runner = null,
        ClusterToolingViewModel? tooling = null,
        ManagedToolStore? store = null)
    {
        var toolRunner = runner ?? new ToolRunner();
        var toolStore = store ?? new ManagedToolStore();

        _provisioners = provisioners ??
        [
            new KindClusterProvisioner(toolRunner, toolStore),
            new MinikubeClusterProvisioner(toolRunner, toolStore),
        ];

        _check = new ToolReadinessCheck(toolRunner, toolStore);
        Tooling = tooling ?? new ClusterToolingViewModel(toolRunner, store: toolStore);
    }

    /// <summary>The KON-109 page, kept whole. Shown in full behind "Manage tooling".</summary>
    public ClusterToolingViewModel Tooling { get; }

    /// <summary>
    /// What the shell is talking to right now, asked each time rather than handed over once: this page
    /// outlives several switches, and a value copied at construction would mark the wrong row.
    /// </summary>
    public Func<string?>? ActiveBackendNow { get; init; }

    /// <summary>
    /// Switches to a cluster; the shell owns the backend list. Answers whether it happened — a cluster
    /// whose control plane is still settling is not connected yet, and the page keeps offering it.
    /// </summary>
    public Func<string, Task<bool>>? RequestUseBackend { get; init; }

    /// <summary>
    /// A cluster appeared or disappeared. The shell re-reads the kubeconfigs and rebuilds the
    /// switcher — the provisioner never touches the registry itself.
    /// </summary>
    public Func<Task>? RequestClustersChanged { get; init; }

    /// <summary>
    /// Makes a just-created cluster visible in the switcher (KON-120's one deliberate exception).
    /// Having to tick a box for a cluster you made here would be the dead-button mistake again.
    /// </summary>
    public Action<string>? RequestShowCluster { get; init; }

    public ObservableCollection<LocalClusterRowViewModel> Clusters { get; } = [];

    /// <summary>Every provisioner Kontena knows, with the state of its tool on this machine.</summary>
    public ObservableCollection<ProvisionerChoiceViewModel> Provisioners { get; } = [];

    [ObservableProperty] private LocalClustersStage _stage = LocalClustersStage.List;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasLoaded;
    [ObservableProperty] private bool _isToolingShown;
    [ObservableProperty] private string? _error;

    /// <summary>Whether anything here can build a cluster. Decides between "make one" and "get a tool".</summary>
    [ObservableProperty] private bool _canProvision;

    [ObservableProperty] private string _toolSummary = string.Empty;

    public bool IsList => Stage == LocalClustersStage.List;
    public bool IsForm => Stage == LocalClustersStage.Form;
    public bool IsRunning => Stage == LocalClustersStage.Running;
    public bool IsFailed => Stage == LocalClustersStage.Failed;

    public bool HasClusters => Clusters.Count > 0;
    public bool IsEmpty => HasLoaded && !HasClusters && CanProvision;

    /// <summary>Nothing can be made and nothing exists — the page is about getting a tool first.</summary>
    public bool NeedsTooling => HasLoaded && !CanProvision;

    partial void OnStageChanged(LocalClustersStage value)
    {
        OnPropertyChanged(nameof(IsList));
        OnPropertyChanged(nameof(IsForm));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsFailed));
    }

    partial void OnCanProvisionChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(NeedsTooling));
    }

    partial void OnHasLoadedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(NeedsTooling));
    }

    [RelayCommand]
    private void ToggleTooling() => IsToolingShown = !IsToolingShown;

    /// <summary>
    /// Re-read the machine: which tools are here, and which clusters exist. Clears the error, because
    /// this is a fresh attempt and the previous failure stops being news.
    /// </summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        if (IsLoading || IsRunning)
            return;

        Error = null;
        IsLoading = true;

        try
        {
            await Tooling.LoadAsync();
            await RefreshToolStateAsync();
            await RefreshClustersAsync();
            HasLoaded = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task RefreshToolStateAsync()
    {
        Provisioners.Clear();

        var summaries = new List<string>();

        foreach (var provisioner in _provisioners)
        {
            var readiness = await provisioner.CheckAsync();
            var purpose = Purposes.TryGetValue(provisioner.Provisioner, out var text) ? text : string.Empty;
            var choice = new ProvisionerChoiceViewModel(provisioner, readiness, purpose);

            Provisioners.Add(choice);
            summaries.Add($"{choice.Name} {choice.State}");
        }

        CanProvision = Provisioners.Any(p => p.IsUsable);
        ToolSummary = string.Join("   ·   ", summaries);

        _availableRuntimes = await AvailableRuntimesAsync();
    }

    /// <summary>
    /// Which runtimes this machine could actually host nodes on. Docker is assumed present — Kontena is
    /// a container app, and where it is not there the tool says so in its own words. Podman is checked
    /// through the tool seam, and kvm2 is Linux-only by construction.
    /// </summary>
    private async Task<IReadOnlyList<LocalClusterRuntime>> AvailableRuntimesAsync()
    {
        var runtimes = new List<LocalClusterRuntime> { LocalClusterRuntime.Docker };

        if ((await _check.CheckAsync(KnownTools.Podman)).Usable)
            runtimes.Add(LocalClusterRuntime.Podman);

        if (OperatingSystem.IsLinux())
            runtimes.Add(LocalClusterRuntime.Kvm2);

        return runtimes;
    }

    private async Task RefreshClustersAsync()
    {
        var found = new List<LocalCluster>();

        foreach (var provisioner in _provisioners)
        {
            // One tool being absent or broken must not cost the other's clusters: an empty list from a
            // provisioner is a normal answer, and a throw here would empty the whole page.
            try
            {
                found.AddRange(await provisioner.ListAsync());
            }
            catch (ToolFailedException)
            {
            }
        }

        Clusters.Clear();
        foreach (var cluster in found)
        {
            Clusters.Add(new LocalClusterRowViewModel(
                cluster,
                BackendFor(cluster) == ActiveBackend,
                CapabilitiesFor(cluster),
                UseAsync,
                DeleteAsync,
                StartAsync,
                StopAsync));
        }

        OnPropertyChanged(nameof(HasClusters));
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>The provisioner that owns this cluster, or null when it came from a tool we dropped.</summary>
    private IClusterProvisioner? ProvisionerFor(LocalCluster cluster) =>
        _provisioners.FirstOrDefault(p =>
            string.Equals(p.Provisioner, cluster.Provisioner, StringComparison.Ordinal));

    private ProvisionerCapabilities CapabilitiesFor(LocalCluster cluster) =>
        ProvisionerFor(cluster)?.Capabilities ?? new ProvisionerCapabilities();

    private async Task UseAsync(LocalClusterRowViewModel row)
    {
        if (RequestUseBackend is not null)
            await RequestUseBackend(BackendFor(row.Cluster));
    }

    /// <summary>Which backend is active right now, for marking the row that is already open.</summary>
    private string? ActiveBackend => ActiveBackendNow?.Invoke();

    /// <summary>
    /// Delete, confirmed as the data loss it is (KON-126). The message says what goes with it, because
    /// "are you sure" tells nobody what they are about to lose.
    /// </summary>
    private Task DeleteAsync(LocalClusterRowViewModel row)
    {
        var nodes = row.Cluster.Nodes.Count;
        var detail = nodes > 0
            ? $"Its {nodes} node containers are stopped and removed, "
            : "Its node containers are stopped and removed, ";

        Confirm(
            $"Delete cluster \"{row.Name}\"?",
            $"Everything running in it goes with it and cannot be brought back. {detail}" +
            $"and the kubeconfig context \"{row.Context}\" is removed.",
            "Delete cluster",
            () => DeleteCoreAsync(row));

        return Task.CompletedTask;
    }

    private async Task DeleteCoreAsync(LocalClusterRowViewModel row)
    {
        Error = null;

        if (ProvisionerFor(row.Cluster) is not { } provisioner)
            return;

        try
        {
            await provisioner.DeleteAsync(row.Name);
        }
        catch (Exception ex) when (ex is ToolFailedException or ToolNotFoundException)
        {
            Error = ex.Message;
            return;
        }

        Clusters.Remove(row);
        OnPropertyChanged(nameof(HasClusters));
        OnPropertyChanged(nameof(IsEmpty));

        if (RequestClustersChanged is not null)
            await RequestClustersChanged();

        await RefreshClustersAsync();
    }

    /// <summary>The backend id discovery gives this cluster's context, so a row can find its switcher entry.</summary>
    private static string BackendFor(LocalCluster cluster)
        => $"{KubernetesAdapterModule.BackendId}:{cluster.Context}";

    public void Dispose()
    {
        _running?.Cancel();
        _running?.Dispose();
        _running = null;
        Tooling.Dispose();
    }
}
