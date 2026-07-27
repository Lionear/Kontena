using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Adapters.LocalClusters;
using Kontena.Core.Orchestration.Provisioning;
using Kontena.Core.Tooling;

namespace Kontena.App.ViewModels;

/// <summary>
/// The create form (KON-76, KON-77). Everything but the name has a default that builds a working
/// single-node cluster, so the only decision required is what to call it.
/// </summary>
/// <remarks>
/// Its own view model rather than more state on the page: this is a form with validation, a preview
/// and a child collection, and the page around it has a life cycle of its own (a create keeps running
/// while the form is gone).
/// <para>
/// What it shows follows the chosen provisioner's <see cref="ProvisionerCapabilities"/> rather than a
/// list of ifs about kind and minikube — a field the tool cannot honour is left out, not sent and
/// rejected.
/// </para>
/// </remarks>
public sealed partial class NewClusterViewModel : ObservableObject
{
    /// <summary>
    /// Offered versions. Deliberately short and led by "whatever the tool ships with": a list we
    /// maintain goes stale, and the tool's own default is the version its release was tested against.
    /// </summary>
    public static readonly IReadOnlyList<string> OfferedVersions =
        ["Default for this release", "v1.34.0", "v1.33.4", "v1.32.5", "v1.31.0"];

    private readonly IReadOnlyList<LocalClusterRuntime> _available;

    public NewClusterViewModel(
        IReadOnlyList<ProvisionerChoiceViewModel> provisioners,
        IReadOnlyList<LocalClusterRuntime> availableRuntimes)
    {
        ArgumentNullException.ThrowIfNull(provisioners);
        ArgumentNullException.ThrowIfNull(availableRuntimes);

        _available = availableRuntimes;
        Provisioners = [.. provisioners];
        Selected = Provisioners.FirstOrDefault(p => p.IsUsable) ?? Provisioners.FirstOrDefault();
        AddPort();
    }

    public ObservableCollection<ProvisionerChoiceViewModel> Provisioners { get; }

    [ObservableProperty] private ProvisionerChoiceViewModel? _selected;

    public ProvisionerCapabilities Capabilities => Selected?.Capabilities ?? new ProvisionerCapabilities();

    // What the chosen tool can be asked for. Bound directly, so a field simply is not there when the
    // tool cannot honour it.
    public bool ShowMultiNode => Capabilities.MultiNode;
    public bool ShowHighAvailability => Capabilities.HighAvailability;
    public bool ShowPorts => Capabilities.PortMappings;
    public bool ShowIngress => Capabilities.IngressReady;
    public bool ShowVersion => Capabilities.KubernetesVersion;
    public bool ShowResources => Capabilities.Resources;
    public bool ShowRuntimes => Runtimes.Count > 1;
    public bool ShowProvisioners => Provisioners.Count > 1;

    /// <summary>
    /// The runtimes worth offering: what the provisioner supports, kept to what this machine has. A
    /// driver that is not installed is a choice that fails after the form is filled in.
    /// </summary>
    public IReadOnlyList<LocalClusterRuntime> Runtimes =>
        [.. Capabilities.Runtimes.Where(_available.Contains)];

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _version = OfferedVersions[0];
    [ObservableProperty] private string _nodeImage = string.Empty;
    [ObservableProperty] private int _controlPlaneNodes = 1;
    [ObservableProperty] private string _workerNodes = "0";
    [ObservableProperty] private string _cpus = string.Empty;
    [ObservableProperty] private string _memoryMb = string.Empty;
    [ObservableProperty] private bool _ingressReady;
    [ObservableProperty] private LocalClusterRuntime _runtime = LocalClusterRuntime.Default;
    [ObservableProperty] private bool _waitForReady = true;

    public IReadOnlyList<string> Versions { get; } = OfferedVersions;

    /// <summary>One or three: two control-plane nodes is a quorum of two, which is worse than one.</summary>
    public IReadOnlyList<int> ControlPlaneChoices { get; } = [1, 3];

    public ObservableCollection<PortMappingRowViewModel> Ports { get; } = [];

    /// <summary>What is wrong with the name, or null. Shown under the field as it is typed.</summary>
    public string? NameProblem => Name.Length == 0 ? null : LocalClusterName.Problem(Name);

    public bool HasNameProblem => NameProblem is not null;

    /// <summary>The context this will write, so the name field says what it is really deciding.</summary>
    public string ContextPreview => LocalClusterName.IsValid(Name) && Selected is { } choice
        ? choice.Id == MinikubeClusterProvisioner.Id
            ? MinikubeClusterProvisioner.ContextFor(Name)
            : KindClusterProvisioner.ContextFor(Name)
        : string.Empty;

    public bool HasContextPreview => ContextPreview.Length > 0;

    /// <summary>A port pair that was half typed. Blocks the create rather than being dropped silently.</summary>
    public bool HasPortProblem => ShowPorts && Ports.Any(p => p.IsIncomplete);

    /// <summary>A resource field that is not a number in range. Same treatment as a half-typed port.</summary>
    public bool HasResourceProblem =>
        ShowResources && (Optional(Cpus, 1, 64) is null || Optional(MemoryMb, 512, 262144) is null);

    public bool CanCreate =>
        Selected is { IsUsable: true }
        && LocalClusterName.IsValid(Name)
        && !HasPortProblem
        && !HasResourceProblem
        && WorkerCount is not null;

    /// <summary>The command as a person would type it — for reading before it runs.</summary>
    public string CommandPreview
    {
        get
        {
            var spec = Build() ?? new LocalClusterSpec("cluster");

            if (Selected?.Id == MinikubeClusterProvisioner.Id)
                return ToolCommand.Describe("minikube", MinikubeArguments.Create(spec));

            var arguments = KindArguments.Create(spec, KindConfig.Needed(spec) ? "<generated>" : null);
            return ToolCommand.Describe("kind", arguments);
        }
    }

    /// <summary>The config file this spec needs, or null when the flags cover it.</summary>
    public string? ConfigPreview
    {
        get
        {
            // minikube takes all of this as flags; only kind needs a file.
            if (Selected?.Id == MinikubeClusterProvisioner.Id)
                return null;

            var spec = Build();
            return spec is not null && KindConfig.Needed(spec) ? KindConfig.Write(spec) : null;
        }
    }

    public bool HasConfigPreview => ConfigPreview is not null;

    [ObservableProperty] private bool _isConfigShown;

    [RelayCommand]
    private void ToggleConfig() => IsConfigShown = !IsConfigShown;

    [RelayCommand]
    private void SelectProvisioner(ProvisionerChoiceViewModel? choice)
    {
        if (choice is { IsUsable: true })
            Selected = choice;
    }

    [RelayCommand]
    private void SelectRuntime(string? runtime)
    {
        if (Enum.TryParse<LocalClusterRuntime>(runtime, out var parsed))
            Runtime = parsed;
    }

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
        if (!LocalClusterName.IsValid(Name) || HasPortProblem || HasResourceProblem
            || WorkerCount is not { } workers)
        {
            return null;
        }

        return new LocalClusterSpec(Name)
        {
            KubernetesVersion = !ShowVersion || Version == OfferedVersions[0] ? null : Version,
            NodeImage = string.IsNullOrWhiteSpace(NodeImage) ? null : NodeImage.Trim(),
            ControlPlaneNodes = ShowHighAvailability ? ControlPlaneNodes : 1,
            WorkerNodes = ShowMultiNode ? workers : 0,
            PortMappings = ShowPorts ? [.. Ports.Select(p => p.Mapping).OfType<ClusterPortMapping>()] : [],
            IngressReady = ShowIngress && IngressReady,
            Runtime = Runtimes.Contains(Runtime) ? Runtime : LocalClusterRuntime.Default,
            Cpus = ShowResources ? Empty(Cpus) : null,
            MemoryMb = ShowResources ? Empty(MemoryMb) : null,

            // Five minutes, and only when asked: the tool returns as soon as the nodes are up, and
            // waiting is the difference between "it exists" and "you can use it".
            ReadyTimeout = WaitForReady ? TimeSpan.FromMinutes(5) : null,
        };
    }

    private int? WorkerCount =>
        int.TryParse(WorkerNodes, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        && value is >= 0 and <= 20
            ? value
            : null;

    /// <summary>A resource field that was left empty means "the tool's default", i.e. null in the spec.</summary>
    private static int? Empty(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? null
            : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;

    /// <summary>
    /// An optional number: empty is fine, a number in range is fine, anything else is null — which the
    /// validation reads as "not usable yet" rather than silently dropping what was typed.
    /// </summary>
    private static int? Optional(string text, int min, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
               && value >= min && value <= max
            ? value
            : null;
    }

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(NameProblem));
        OnPropertyChanged(nameof(HasNameProblem));
        OnPropertyChanged(nameof(ContextPreview));
        OnPropertyChanged(nameof(HasContextPreview));
        Recompute();
    }

    partial void OnSelectedChanged(ProvisionerChoiceViewModel? value)
    {
        foreach (var choice in Provisioners)
            choice.IsSelected = ReferenceEquals(choice, value);

        // The runtime that was picked may not exist for this tool — fall back rather than sending it a
        // driver it does not know.
        if (!Runtimes.Contains(Runtime))
            Runtime = LocalClusterRuntime.Default;

        foreach (var name in new[]
                 {
                     nameof(Capabilities), nameof(ShowMultiNode), nameof(ShowHighAvailability),
                     nameof(ShowPorts), nameof(ShowIngress), nameof(ShowVersion), nameof(ShowResources),
                     nameof(ShowRuntimes), nameof(Runtimes), nameof(ContextPreview),
                     nameof(HasContextPreview), nameof(HasDocker), nameof(HasPodman), nameof(HasKvm2),
                 })
        {
            OnPropertyChanged(name);
        }

        Recompute();
    }

    partial void OnVersionChanged(string value) => Recompute();
    partial void OnNodeImageChanged(string value) => Recompute();
    partial void OnControlPlaneNodesChanged(int value) => Recompute();
    partial void OnWorkerNodesChanged(string value) => Recompute();
    partial void OnCpusChanged(string value) => Recompute();
    partial void OnMemoryMbChanged(string value) => Recompute();
    partial void OnIngressReadyChanged(bool value) => Recompute();
    partial void OnRuntimeChanged(LocalClusterRuntime value) => Recompute();
    partial void OnWaitForReadyChanged(bool value) => Recompute();

    private void Recompute()
    {
        OnPropertyChanged(nameof(HasPortProblem));
        OnPropertyChanged(nameof(HasResourceProblem));
        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(CommandPreview));
        OnPropertyChanged(nameof(ConfigPreview));
        OnPropertyChanged(nameof(HasConfigPreview));
        OnPropertyChanged(nameof(IsDefaultRuntime));
        OnPropertyChanged(nameof(IsDockerRuntime));
        OnPropertyChanged(nameof(IsPodmanRuntime));
        OnPropertyChanged(nameof(IsKvm2Runtime));
    }

    // Radio state, one property each: an enum does not bind to IsChecked, and a converter for three
    // values is more moving parts than three getters.
    public bool IsDefaultRuntime => Runtime == LocalClusterRuntime.Default;
    public bool IsDockerRuntime => Runtime == LocalClusterRuntime.Docker;
    public bool IsPodmanRuntime => Runtime == LocalClusterRuntime.Podman;
    public bool IsKvm2Runtime => Runtime == LocalClusterRuntime.Kvm2;

    public bool HasDocker => Runtimes.Contains(LocalClusterRuntime.Docker);
    public bool HasPodman => Runtimes.Contains(LocalClusterRuntime.Podman);
    public bool HasKvm2 => Runtimes.Contains(LocalClusterRuntime.Kvm2);
}
