using CommunityToolkit.Mvvm.ComponentModel;
using Kontena.Sdk.Orchestration.Provisioning;
using Kontena.Sdk.Tooling;

namespace Kontena.App.ViewModels;

/// <summary>
/// One distribution as the provisioning wizard offers it — k0s today, kubeadm and Talos later
/// (KON-379).
/// <para>
/// A sibling of <see cref="ProvisionerChoiceViewModel"/> rather than a generic version of it, for the
/// same reason the contracts are siblings: it holds an <see cref="IRemoteClusterProvisioner"/>, and the
/// step that follows this one needs that instance to ask for a preview and a rollout. The one thing
/// the two rows genuinely share — how a tool's state is worded — is shared, not copied.
/// </para>
/// </summary>
public sealed partial class RemoteProvisionerChoiceViewModel(
    IRemoteClusterProvisioner provisioner,
    string purpose) : ObservableObject
{
    public IRemoteClusterProvisioner Provisioner => provisioner;

    /// <summary>
    /// What its tool looks like right now. Starts as "not looked yet" and is filled in by
    /// <see cref="RefreshAsync"/> — finding a tool means running one, and a settings page that blocks
    /// on that while it is being built is a settings page that hangs.
    /// </summary>
    [ObservableProperty] private ToolReadiness? _readiness;

    /// <summary>Asks the tooling seam about this distribution's tool and updates the row.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        Readiness = await provisioner.CheckAsync(ct);

        foreach (var name in new[] { nameof(IsUsable), nameof(State), nameof(Hint), nameof(IsChecked) })
            OnPropertyChanged(name);
    }

    /// <summary>Whether its tool has been looked for yet. False keeps the row from claiming "missing".</summary>
    public bool IsChecked => Readiness is not null;

    public string Id => provisioner.Provisioner;

    public string Name => provisioner.DisplayName;

    /// <summary>One line on what picking this one means. The trade-off, not marketing.</summary>
    public string Purpose => purpose;

    public ProvisionerCapabilities Capabilities => provisioner.Capabilities;

    /// <summary>How it will reach the machines, which decides what the credentials step asks for.</summary>
    public ProvisionerTransport Transport => Capabilities.Transport;

    /// <summary>Whether it can be picked. False until its tool has been looked for — never a guess.</summary>
    public bool IsUsable => Readiness?.Usable == true;

    /// <summary>The tool's own state, in the same words the local create form uses.</summary>
    public string State => Readiness is { } readiness
        ? ProvisionerChoiceViewModel.Describe(readiness)
        : "looking for its tool…";

    /// <summary>The install to offer when the tool is not here, or null when there is nothing to offer.</summary>
    public InstallHint? Hint => Readiness?.Hint;

    [ObservableProperty] private bool _isSelected;
}
