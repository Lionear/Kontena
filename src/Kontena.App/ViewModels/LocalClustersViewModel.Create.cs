using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Adapters.LocalClusters;
using Kontena.Core.Orchestration.Provisioning;
using Kontena.Core.Tooling;

namespace Kontena.App.ViewModels;

/// <summary>
/// Creating a cluster: the form, the run, and what is left on screen when it fails (KON-76).
/// </summary>
public sealed partial class LocalClustersViewModel
{
    /// <summary>The form, while it is on screen. Null elsewhere so a stale one cannot be submitted.</summary>
    [ObservableProperty] private NewClusterViewModel? _form;

    /// <summary>The tool's own output, line by line, as it arrives.</summary>
    public ObservableCollection<string> Output { get; } = [];

    /// <summary>What is being created right now — the title while it runs.</summary>
    [ObservableProperty] private string _creatingName = string.Empty;

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
        Form = new NewClusterViewModel(_provisioner.Capabilities, _podmanAvailable);
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

    /// <summary>Stops kind. It removes what it made so far itself, which is why this is not a rollback.</summary>
    [RelayCommand]
    private void CancelRun() => _running?.Cancel();

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (Form?.Build() is not { } spec || IsRunning)
            return;

        Output.Clear();
        Error = null;
        FailureHint = null;
        Created = null;
        CreatingName = spec.Name;
        Stage = LocalClustersStage.Running;

        _running?.Dispose();
        _running = new CancellationTokenSource();

        try
        {
            await foreach (var line in _provisioner.CreateAsync(spec, _running.Token))
                Output.Add(line.Text);

            await FinishAsync(spec);
        }
        catch (OperationCanceledException)
        {
            Output.Add("Cancelled.");
            Stage = LocalClustersStage.Failed;
            FailureHint = "Cancelled. kind removes what it had already made.";
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
        // Re-read first, so the context comes from the provisioner rather than from a rule about how
        // it names things. Ask the tool what it made; do not assume.
        await RefreshClustersAsync();

        var row = Clusters.FirstOrDefault(c => string.Equals(c.Name, spec.Name, StringComparison.Ordinal))
                  ?? new LocalClusterRowViewModel(
                      new LocalCluster(spec.Name, _provisioner.Provisioner, KindClusterProvisioner.ContextFor(spec.Name)),
                      isActive: false, UseAsync, DeleteAsync);

        // Visible before the rebuild: the rebuild reads this setting, and doing it the other way round
        // would leave the cluster out of the switcher until something else triggered another one.
        RequestShowCluster?.Invoke(BackendFor(row.Cluster));

        if (RequestClustersChanged is not null)
            await RequestClustersChanged();

        Form = null;
        Stage = LocalClustersStage.List;
        Created = row;
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
    private static string? Explain(string complaint, LocalClusterSpec spec)
    {
        if (complaint.Contains("port is already allocated", StringComparison.OrdinalIgnoreCase)
            || complaint.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
        {
            var ports = string.Join(", ", spec.PortMappings.Select(p => p.HostPort));
            return ports.Length > 0
                ? $"A host port is already taken ({ports}). Free it, or map a different one."
                : "A host port is already taken. Free it, or map a different one.";
        }

        if (complaint.Contains("already exist", StringComparison.OrdinalIgnoreCase))
            return $"A cluster called \"{spec.Name}\" already exists. Pick another name, or delete that one first.";

        if (complaint.Contains("Cannot connect to the Docker daemon", StringComparison.OrdinalIgnoreCase)
            || complaint.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
            && complaint.Contains("docker.sock", StringComparison.OrdinalIgnoreCase))
        {
            return "The container runtime did not answer. Start it, then try again.";
        }

        if (complaint.Contains("no space left on device", StringComparison.OrdinalIgnoreCase))
            return "The disk is full. A node image needs about a gigabyte.";

        return null;
    }
}
