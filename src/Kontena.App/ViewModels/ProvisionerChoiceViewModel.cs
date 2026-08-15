using CommunityToolkit.Mvvm.ComponentModel;
using Kontena.Sdk.Orchestration.Provisioning;
using Kontena.Sdk.Tooling;

namespace Kontena.App.ViewModels;

/// <summary>
/// One provisioner as the create form offers it: what it is, what it can do, and whether its tool is
/// on this machine (KON-77).
/// </summary>
public sealed partial class ProvisionerChoiceViewModel(
    IClusterProvisioner provisioner,
    ToolReadiness readiness,
    string purpose,
    ClusterVersionOptions? versions = null) : ObservableObject
{
    public IClusterProvisioner Provisioner => provisioner;

    /// <summary>
    /// The versions this tool offers, read once when the page loaded (KON-144). Fetched there rather
    /// than here because asking minikube means running it, and a form that has to await something
    /// before it can draw a dropdown is a form that flickers.
    /// </summary>
    public ClusterVersionOptions Versions => versions ?? ClusterVersionOptions.None;

    public string Id => provisioner.Provisioner;

    public string Name => provisioner.DisplayName;

    /// <summary>One line on what picking this one means. Not marketing — the trade-off.</summary>
    public string Purpose => purpose;

    public ProvisionerCapabilities Capabilities => provisioner.Capabilities;

    /// <summary>Whether it can be picked at all.</summary>
    public bool IsUsable => readiness.Usable;

    /// <summary>The version, or why it cannot be picked — the same line the summary row shows.</summary>
    public string State => Describe(readiness);

    /// <summary>
    /// The one wording for a tool's state on a provisioner row. Static because the remote wizard shows
    /// the same row for <c>k0sctl</c> (KON-379), and two spellings of "not installed" is one more than
    /// anyone needs.
    /// </summary>
    public static string Describe(ToolReadiness readiness)
    {
        ArgumentNullException.ThrowIfNull(readiness);

        return readiness.State switch
        {
            ToolState.Ready or ToolState.Outdated =>
                ToolReadinessCheck.Number(readiness.Version) is { } number ? $"v{number}" : "installed",
            ToolState.Unusable => "installed but will not run",
            _ => "not installed",
        };
    }

    [ObservableProperty] private bool _isSelected;
}
