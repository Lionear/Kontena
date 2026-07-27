using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Orchestration.Provisioning;
using Kontena.Core.Tooling;

namespace Kontena.App.ViewModels;

/// <summary>
/// Creating a cluster, starting a stopped one, and what is left on screen when either fails
/// (KON-76, KON-77).
/// </summary>
public sealed partial class LocalClustersViewModel
{
    /// <summary>The form, while it is on screen. Null elsewhere so a stale one cannot be submitted.</summary>
    [ObservableProperty] private NewClusterViewModel? _form;

    /// <summary>The tool's own output, line by line, as it arrives.</summary>
    public ObservableCollection<string> Output { get; } = [];

    /// <summary>What is being created or started right now — the title while it runs.</summary>
    [ObservableProperty] private string _creatingName = string.Empty;

    /// <summary>True while the running thing is a start rather than a create; the wording differs.</summary>
    [ObservableProperty] private bool _isStarting;

    /// <summary>A one-line reading of a failure, when the output has a known shape.</summary>
    [ObservableProperty] private string? _failureHint;

    /// <summary>The cluster that was just created, so the page can offer to switch to it.</summary>
    [ObservableProperty] private LocalClusterRowViewModel? _created;

    public bool HasCreated => Created is not null;

    partial void OnCreatedChanged(LocalClusterRowViewModel? value) => OnPropertyChanged(nameof(HasCreated));

    [RelayCommand]
    private void NewCluster()
    {
        if (!CanProvision)
            return;

        Created = null;
        Error = null;
        Form = new NewClusterViewModel([.. Provisioners], _availableRuntimes);
        Stage = LocalClustersStage.Form;
    }

    [RelayCommand]
    private void CancelForm()
    {
        Form = null;
        Stage = LocalClustersStage.List;
    }

    /// <summary>Back to the form with what was typed still in it — a failure is usually one field.</summary>
    [RelayCommand]
    private void EditAndRetry() => Stage = Form is null ? LocalClustersStage.List : LocalClustersStage.Form;

    [RelayCommand]
    private void BackToList()
    {
        Form = null;
        Stage = LocalClustersStage.List;
    }

    /// <summary>Stops the tool. It removes what it made so far itself, which is why this is not a rollback.</summary>
    [RelayCommand]
    private void CancelRun() => _running?.Cancel();

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (Form is not { Selected.Provisioner: { } provisioner } form
            || form.Build() is not { } spec
            || IsRunning)
        {
            return;
        }

        await RunAsync(spec.Name, starting: false, ct => provisioner.CreateAsync(spec, ct), spec);
    }

    /// <summary>
    /// Start a stopped cluster. Streamed like a create, because it is one in every way that matters to
    /// someone watching: a control plane coming up takes its time.
    /// </summary>
    private async Task StartAsync(LocalClusterRowViewModel row)
    {
        if (ProvisionerFor(row.Cluster) is not { } provisioner || !provisioner.Capabilities.StartStop)
            return;

        await RunAsync(row.Name, starting: true, ct => provisioner.StartAsync(row.Name, ct), spec: null);
    }

    /// <summary>
    /// Stop a running cluster. Confirmed, but not as data loss (KON-126): nothing is lost, the
    /// workloads come back with it — and a dialog that cries wolf teaches people to click it away.
    /// </summary>
    private Task StopAsync(LocalClusterRowViewModel row)
    {
        if (ProvisionerFor(row.Cluster) is not { } provisioner || !provisioner.Capabilities.StartStop)
            return Task.CompletedTask;

        Confirm(
            $"Stop cluster \"{row.Name}\"?",
            "Everything in it stops until you start it again. Nothing is deleted, and it comes back as it was.",
            "Stop cluster",
            async () =>
            {
                Error = null;

                try
                {
                    await provisioner.StopAsync(row.Name);
                }
                catch (Exception ex) when (ex is ToolFailedException or ToolNotFoundException)
                {
                    Error = ex.Message;
                    return;
                }

                await RefreshClustersAsync();
            },
            destructive: false);

        return Task.CompletedTask;
    }

    /// <summary>
    /// The shared shape of a long, streamed run: clear the console, show it, and end in the list or in
    /// the failure state with the tool's own words still on screen.
    /// </summary>
    private async Task RunAsync(
        string name,
        bool starting,
        Func<CancellationToken, IAsyncEnumerable<ToolLine>> run,
        LocalClusterSpec? spec)
    {
        Output.Clear();
        Error = null;
        FailureHint = null;
        Created = null;
        CreatingName = name;
        IsStarting = starting;
        Stage = LocalClustersStage.Running;

        _running?.Dispose();
        _running = new CancellationTokenSource();

        try
        {
            await foreach (var line in run(_running.Token))
                Output.Add(line.Text);

            if (spec is not null)
                await FinishAsync(spec);
            else
                await FinishStartAsync(name);
        }
        catch (OperationCanceledException)
        {
            Output.Add("Cancelled.");
            Stage = LocalClustersStage.Failed;
            FailureHint = starting
                ? "Cancelled. The cluster is left as it was."
                : "Cancelled. The tool removes what it had already made.";
        }
        catch (ToolFailedException ex)
        {
            Stage = LocalClustersStage.Failed;
            Error = ex.Complaint;
            FailureHint = Explain(ex.Complaint, spec);
        }
        catch (ToolNotFoundException ex)
        {
            Stage = LocalClustersStage.Failed;
            Error = ex.Message;
            FailureHint = "The tool is gone since this page was opened. Re-check under Manage tooling.";
        }
        finally
        {
            _running?.Dispose();
            _running = null;
        }
    }

    /// <summary>
    /// A finished create. The cluster is made visible and the switcher rebuilt before the list is
    /// re-read, so the row and the switcher entry appear together rather than a beat apart.
    /// </summary>
    private async Task FinishAsync(LocalClusterSpec spec)
    {
        // Re-read first, so the context comes from the provisioner rather than from a rule about how it
        // names things. Ask the tool what it made; do not assume.
        await RefreshClustersAsync();

        var row = Clusters.FirstOrDefault(c => string.Equals(c.Name, spec.Name, StringComparison.Ordinal));

        // Visible before the rebuild: the rebuild reads this setting, and doing it the other way round
        // would leave the cluster out of the switcher until something else triggered another one.
        if (row is not null)
            RequestShowCluster?.Invoke($"{Kontena.Adapters.Kubernetes.KubernetesAdapterModule.BackendId}:{row.Context}");

        if (RequestClustersChanged is not null)
            await RequestClustersChanged();

        Form = null;
        Stage = LocalClustersStage.List;

        if (row is null)
            return;

        // Go straight to it. You did not make a cluster to look at a list — and the alternative is
        // landing back in Settings, which is where the create started, not where it ended.
        var switched = RequestUseBackend is not null
                       && await RequestUseBackend($"{Kontena.Adapters.Kubernetes.KubernetesAdapterModule.BackendId}:{row.Context}");

        // Only when the switch did not happen is there something left to offer: the control plane can
        // still be settling, and then the banner is the way back to it.
        Created = switched ? null : row;
    }

    /// <summary>
    /// A finished start. Same ending as a create — the cluster is up and this is where you wanted to be
    /// — minus the visibility step, because a cluster that was already listed is already known.
    /// </summary>
    private async Task FinishStartAsync(string name)
    {
        await RefreshClustersAsync();
        Stage = LocalClustersStage.List;

        if (Clusters.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal)) is not { } row)
            return;

        var switched = RequestUseBackend is not null
                       && await RequestUseBackend($"{Kontena.Adapters.Kubernetes.KubernetesAdapterModule.BackendId}:{row.Context}");

        Created = switched ? null : row;
    }

    [RelayCommand]
    private async Task UseCreatedAsync()
    {
        if (Created is { } row)
        {
            await UseAsync(row);
            Created = null;
        }
    }

    [RelayCommand]
    private void DismissCreated() => Created = null;

    /// <summary>
    /// The one sentence that turns a wall of output into a next step. Only for shapes we are sure of —
    /// a wrong reading is worse than none, because it sends someone off to fix the wrong thing.
    /// </summary>
    private static string? Explain(string complaint, LocalClusterSpec? spec)
    {
        if (complaint.Contains("port is already allocated", StringComparison.OrdinalIgnoreCase)
            || complaint.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
        {
            var ports = string.Join(", ", spec?.PortMappings.Select(p => p.HostPort) ?? []);
            return ports.Length > 0
                ? $"A host port is already taken ({ports}). Free it, or map a different one."
                : "A host port is already taken. Free it, or map a different one.";
        }

        if (complaint.Contains("already exist", StringComparison.OrdinalIgnoreCase) && spec is not null)
            return $"A cluster called \"{spec.Name}\" already exists. Pick another name, or delete that one first.";

        if (complaint.Contains("Cannot connect to the Docker daemon", StringComparison.OrdinalIgnoreCase)
            || (complaint.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
                && complaint.Contains("docker.sock", StringComparison.OrdinalIgnoreCase)))
        {
            return "The container runtime did not answer. Start it, then try again.";
        }

        if (complaint.Contains("no space left on device", StringComparison.OrdinalIgnoreCase))
            return "The disk is full. A node image needs about a gigabyte.";

        // minikube's own words for a driver that is not installed or not usable on this machine.
        if (complaint.Contains("DRV_", StringComparison.Ordinal)
            || complaint.Contains("is not installed", StringComparison.OrdinalIgnoreCase))
        {
            return "That driver is not usable on this machine. Pick another one, or install it first.";
        }

        return null;
    }
}
