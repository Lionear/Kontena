using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Adapters.LocalClusters;
using Kontena.Core.Orchestration.Provisioning;
using Kontena.Core.Tooling;

namespace Kontena.App.ViewModels;

/// <summary>
/// The create form (KON-76). Everything but the name has a default that builds a working single-node
/// cluster, so the only decision required is what to call it.
/// </summary>
/// <remarks>
/// Its own view model rather than more state on the page: this is a form with validation, a preview
/// and a child collection, and the page around it has a life cycle of its own (a create keeps running
/// while the form is gone).
/// </remarks>
public sealed partial class NewClusterViewModel : ObservableObject
{
    /// <summary>
    /// Offered versions. Deliberately short and led by "whatever kind ships with": a list we maintain
    /// goes stale, and the tool's own default is the version its release was tested against.
    /// </summary>
    public static readonly IReadOnlyList<string> OfferedVersions =
        ["Default for this kind release", "v1.34.0", "v1.33.4", "v1.32.5", "v1.31.0"];

    public NewClusterViewModel(ProvisionerCapabilities capabilities, bool podmanAvailable)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        Capabilities = capabilities;
        PodmanAvailable = podmanAvailable;
        AddPort();
    }

    public ProvisionerCapabilities Capabilities { get; }

    /// <summary>Whether Podman is on this machine. Without it, offering the choice is a dead control.</summary>
    public bool PodmanAvailable { get; }

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _version = OfferedVersions[0];
    [ObservableProperty] private string _nodeImage = string.Empty;
    [ObservableProperty] private int _controlPlaneNodes = 1;
    [ObservableProperty] private string _workerNodes = "0";
    [ObservableProperty] private bool _ingressReady;
    [ObservableProperty] private bool _usePodman;
    [ObservableProperty] private bool _waitForReady = true;

    public IReadOnlyList<string> Versions { get; } = OfferedVersions;

    /// <summary>One or three: two control-plane nodes is a quorum of two, which is worse than one.</summary>
    public IReadOnlyList<int> ControlPlaneChoices { get; } = [1, 3];

    public ObservableCollection<PortMappingRowViewModel> Ports { get; } = [];

    /// <summary>What is wrong with the name, or null. Shown under the field as it is typed.</summary>
    public string? NameProblem => Name.Length == 0 ? null : LocalClusterName.Problem(Name);

    public bool HasNameProblem => NameProblem is not null;

    /// <summary>The context this will write, so the name field says what it is really deciding.</summary>
    public string ContextPreview =>
        LocalClusterName.IsValid(Name) ? KindClusterProvisioner.ContextFor(Name) : string.Empty;

    public bool HasContextPreview => ContextPreview.Length > 0;

    /// <summary>A port pair that was half typed. Blocks the create rather than being dropped silently.</summary>
    public bool HasPortProblem => Ports.Any(p => p.IsIncomplete);

    public bool CanCreate => LocalClusterName.IsValid(Name) && !HasPortProblem && WorkerCount is not null;

    /// <summary>The command as a person would type it — for reading before it runs.</summary>
    public string CommandPreview
    {
        get
        {
            var spec = Build() ?? new LocalClusterSpec("cluster");
            var arguments = KindArguments.Create(spec, KindConfig.Needed(spec) ? "<generated>" : null);
            return ToolCommand.Describe("kind", arguments);
        }
    }

    /// <summary>The config file this spec needs, or null when the flags cover it.</summary>
    public string? ConfigPreview
    {
        get
        {
            var spec = Build();
            return spec is not null && KindConfig.Needed(spec) ? KindConfig.Write(spec) : null;
        }
    }

    public bool HasConfigPreview => ConfigPreview is not null;

    [ObservableProperty] private bool _isConfigShown;

    [RelayCommand]
    private void ToggleConfig() => IsConfigShown = !IsConfigShown;

    [RelayCommand]
    private void AddPort()
    {
        var row = new PortMappingRowViewModel(Remove);
        row.Edited += (_, _) => Recompute();
        Ports.Add(row);
        Recompute();
    }

    private void Remove(PortMappingRowViewModel row)
    {
        Ports.Remove(row);

        // Never leave the section without a row: an empty list gives nothing to type in, and "Add port"
        // as the only affordance reads as a feature that is off.
        if (Ports.Count == 0)
            AddPort();
        else
            Recompute();
    }

    /// <summary>The spec, or null when the form is not usable yet.</summary>
    public LocalClusterSpec? Build()
    {
        if (!LocalClusterName.IsValid(Name) || HasPortProblem || WorkerCount is not { } workers)
            return null;

        return new LocalClusterSpec(Name)
        {
            KubernetesVersion = Version == OfferedVersions[0] ? null : Version,
            NodeImage = string.IsNullOrWhiteSpace(NodeImage) ? null : NodeImage.Trim(),
            ControlPlaneNodes = ControlPlaneNodes,
            WorkerNodes = workers,
            PortMappings = [.. Ports.Select(p => p.Mapping).OfType<ClusterPortMapping>()],
            IngressReady = IngressReady,
            Runtime = UsePodman ? LocalClusterRuntime.Podman : LocalClusterRuntime.Docker,

            // Five minutes, and only when asked: kind returns as soon as the nodes are up, and waiting
            // is the difference between "it exists" and "you can use it".
            ReadyTimeout = WaitForReady ? TimeSpan.FromMinutes(5) : null,
        };
    }

    private int? WorkerCount =>
        int.TryParse(WorkerNodes, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        && value is >= 0 and <= 20
            ? value
            : null;

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(NameProblem));
        OnPropertyChanged(nameof(HasNameProblem));
        OnPropertyChanged(nameof(ContextPreview));
        OnPropertyChanged(nameof(HasContextPreview));
        Recompute();
    }

    partial void OnVersionChanged(string value) => Recompute();
    partial void OnNodeImageChanged(string value) => Recompute();
    partial void OnControlPlaneNodesChanged(int value) => Recompute();
    partial void OnWorkerNodesChanged(string value) => Recompute();
    partial void OnIngressReadyChanged(bool value) => Recompute();
    partial void OnUsePodmanChanged(bool value) => Recompute();
    partial void OnWaitForReadyChanged(bool value) => Recompute();

    private void Recompute()
    {
        OnPropertyChanged(nameof(HasPortProblem));
        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(CommandPreview));
        OnPropertyChanged(nameof(ConfigPreview));
        OnPropertyChanged(nameof(HasConfigPreview));
    }
}
