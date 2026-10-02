using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Adapters.Kubernetes;
using Kontena.Core.Orchestration.Provisioning;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;
using Kontena.Sdk.Orchestration.Provisioning;
using Kontena.Sdk.Tooling;

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

    /// <summary>
    /// How to reach a cluster that has just been made, by kubeconfig context. Its own hook because the
    /// post-create apply needs an engine before the shell has switched to anything — and so a test can
    /// hand over a fake instead of a real API server.
    /// </summary>
    public Func<string, IClusterEngine>? EngineFor { get; init; }

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

        await RunAsync(spec.Name, starting: false, ct => provisioner.CreateAsync(spec, ct), spec, provisioner);
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
        LocalClusterSpec? spec,
        IClusterProvisioner? provisioner = null)
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
            {
                if (provisioner is not null)
                    await ApplyPostCreateAsync(provisioner, spec, _running.Token);

                await FinishAsync(spec);
            }
            else
            {
                await FinishStartAsync(name);
            }
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
            FailureHint = "The tool is gone since this page was opened. Re-check under Settings › Tools.";
        }
        catch (InvalidOperationException ex)
        {
            // The tool did its half and something after it did not — today that is the post-create apply
            // (KON-465). Worth its own wording: the cluster exists, so "try again" is the wrong advice.
            Stage = LocalClustersStage.Failed;
            Error = ex.Message;
            FailureHint =
                $"The cluster \"{name}\" was created, but what had to go on it did not. Its nodes stay " +
                "NotReady until a network is installed — delete it and try again, or apply the manifest " +
                "yourself.";
        }
        finally
        {
            _running?.Dispose();
            _running = null;
        }
    }

    /// <summary>
    /// What the provisioner asked for after the create — today the CNI a kind cluster was told not to
    /// install itself (KON-465), an add-on chosen at create time after that.
    /// <para>
    /// Through the same declarative core as any other manifest (KON-86), rather than shelling out to
    /// kubectl: server-side apply is what makes a bundle this size land in one pass, and a failure comes
    /// back per resource with the server's own words rather than as a wall of output to read.
    /// </para>
    /// </summary>
    private async Task ApplyPostCreateAsync(
        IClusterProvisioner provisioner, LocalClusterSpec spec, CancellationToken ct)
    {
        var manifests = provisioner.PostCreateManifests(spec);
        if (manifests.Count == 0)
            return;

        // Ask the tool which context it wrote rather than deriving one from the name — the same rule
        // FinishAsync follows, and the reason a provisioner never registers anything itself.
        await RefreshClustersAsync();

        var context = Clusters
            .FirstOrDefault(c => string.Equals(c.Name, spec.Name, StringComparison.Ordinal))?.Context;

        if (string.IsNullOrEmpty(context))
        {
            throw new InvalidOperationException(
                $"\"{spec.Name}\" is not in the kubeconfig yet, so there was nothing to apply to.");
        }

        var engine = (EngineFor ?? Connect)(context);
        try
        {
            foreach (var manifest in manifests)
                await ApplyOneAsync(engine, manifest, ct);
        }
        finally
        {
            (engine as IDisposable)?.Dispose();
        }
    }

    private static KubernetesClusterEngine Connect(string context) => new(context);

    /// <summary>
    /// One manifest, fetched or rendered and then applied, with every resource echoed into the same
    /// console the tool's own output went to. A resource the server rejected fails the whole run: a CNI
    /// that only half arrived is a cluster nobody can use, and saying so is the point.
    /// </summary>
    private async Task ApplyOneAsync(IClusterEngine engine, ClusterManifest manifest, CancellationToken ct)
    {
        Output.Add($"Applying {manifest.DisplayName}…");

        var bundle = await ClusterManifests.ResolveAsync(manifest, ct);
        var failures = new List<string>();

        await foreach (var step in engine.ApplyAsync(bundle, ct: ct))
        {
            Output.Add($"  {step.Resource} {step.Action.ToString().ToLowerInvariant()}");

            if (step.Action == ApplyAction.Failed)
                failures.Add($"{step.Resource}: {step.Error}");
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"{manifest.DisplayName} did not apply cleanly:{System.Environment.NewLine}" +
                string.Join(System.Environment.NewLine, failures));
        }

        // ponytail: no wait for Ready here — the nodes come up as the network's pods do, and the page
        // already handles a cluster that is not connectable yet by offering it in a banner. Poll the node
        // conditions here if that turns out to be worth the wait.
        Output.Add($"{manifest.DisplayName} applied. The nodes report Ready once its pods are up.");
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
