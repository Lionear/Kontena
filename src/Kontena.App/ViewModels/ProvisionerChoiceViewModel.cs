using CommunityToolkit.Mvvm.ComponentModel;
using Kontena.Core.Orchestration.Provisioning;
using Kontena.Core.Tooling;

namespace Kontena.App.ViewModels;

/// <summary>
/// One provisioner as the create form offers it: what it is, what it can do, and whether its tool is
/// on this machine (KON-77).
/// </summary>
public sealed partial class ProvisionerChoiceViewModel(
    IClusterProvisioner provisioner,
    ToolReadiness readiness,
    string purpose) : ObservableObject
{
    public IClusterProvisioner Provisioner => provisioner;

    public string Id => provisioner.Provisioner;

    public string Name => provisioner.DisplayName;

    /// <summary>One line on what picking this one means. Not marketing — the trade-off.</summary>
    public string Purpose => purpose;

    public ProvisionerCapabilities Capabilities => provisioner.Capabilities;

    /// <summary>Whether it can be picked at all.</summary>
    public bool IsUsable => readiness.Usable;

    /// <summary>The version, or why it cannot be picked — the same line the summary row shows.</summary>
    public string State => readiness.State switch
    {
        ToolState.Ready or ToolState.Outdated =>
            ToolReadinessCheck.Number(readiness.Version) is { } number ? $"v{number}" : "installed",
        ToolState.Unusable => "installed but will not run",
        _ => "not installed",
    };

    [ObservableProperty] private bool _isSelected;
}
