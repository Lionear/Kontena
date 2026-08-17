using CommunityToolkit.Mvvm.ComponentModel;
using Kontena.Sdk.Orchestration.Provisioning;

namespace Kontena.App.ViewModels;

/// <summary>
/// How the rollout gets in — the one thing the wizard asks, whichever distribution is chosen
/// (KON-234).
/// <para>
/// Which fields exist follows <see cref="Transport"/>, taken from the provisioner's
/// <see cref="ProvisionerCapabilities"/>. Not from the distribution's name: kubeadm and k0s differ in
/// almost everything else and are identical here, and a form built on <c>if provisioner == "k0s"</c>
/// would have to be edited again for the fourth one.
/// </para>
/// <para>
/// There is no password field, on either form. That is the rule from the ticket made structural — a
/// password the app must hold to reach five machines is the thing not worth holding, and a field that
/// does not exist cannot be added back by accident.
/// </para>
/// </summary>
public sealed partial class ClusterCredentialsViewModel : ObservableObject
{
    public ClusterCredentialsViewModel(ProvisionerTransport transport) => Transport = transport;

    public ProvisionerTransport Transport { get; }

    /// <summary>SSH: the kubeadm and k0s form. Same fields for both, because they need the same thing.</summary>
    public bool IsSsh => Transport == ProvisionerTransport.Ssh;

    /// <summary>Talos: a talosconfig, and no SSH fields at all — there is no shell to offer them for.</summary>
    public bool IsTalos => Transport == ProvisionerTransport.MachineApi;

    /// <summary>A local provisioner reaches nothing, so it asks nothing.</summary>
    public bool NeedsNothing => Transport == ProvisionerTransport.Local;

    // ── SSH ──────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _user = string.Empty;
    [ObservableProperty] private string _keyPath = string.Empty;
    [ObservableProperty] private bool _useSudo = true;

    /// <summary>True while no key file is named, i.e. the agent is expected to answer.</summary>
    public bool UsesAgent => IsSsh && KeyPath.Trim().Length == 0;

    // ── Talos ────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _talosConfigPath = string.Empty;
    [ObservableProperty] private string _talosContext = string.Empty;

    /// <summary>
    /// What the SSH agent is offering, or null when nobody has asked it.
    /// <para>
    /// Defaults to "definitely nothing" when <c>SSH_AUTH_SOCK</c> is unset, because that is knowable
    /// here for free and is the common half of the problem — no agent at all. Whether a running agent
    /// holds <i>this</i> key needs <c>ssh-add -l</c>, which is a machine being probed and therefore
    /// belongs to the preflight (KON-235); until then this stays null and says nothing.
    /// </para>
    /// </summary>
    public IReadOnlyCollection<string>? AgentKeys { get; init; } =
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_AUTH_SOCK")) ? [] : null;

    /// <summary>The credentials this form describes, or null when it does not describe any yet.</summary>
    public IClusterCredentials? Build() => Transport switch
    {
        ProvisionerTransport.Ssh => new SshCredentials(Blank(User))
        {
            KeyPath = Blank(KeyPath),
            UseSudo = UseSudo,
        },
        ProvisionerTransport.MachineApi => new TalosCredentials
        {
            ConfigPath = Blank(TalosConfigPath),
            Context = Blank(TalosContext),
        },
        _ => null,
    };

    /// <summary>What is wrong, or null. The credential type's own rule, never a second copy of it.</summary>
    public string? Problem => Build() switch
    {
        SshCredentials ssh => ssh.Problem(AgentKeys),
        { } other => other.Problem(),
        null => null,
    };

    public bool HasProblem => Problem is not null;

    /// <summary>Whether the wizard may go on.</summary>
    public bool IsUsable => Problem is null;

    partial void OnUserChanged(string value) => Recompute();
    partial void OnKeyPathChanged(string value) => Recompute();
    partial void OnUseSudoChanged(bool value) => Recompute();
    partial void OnTalosConfigPathChanged(string value) => Recompute();
    partial void OnTalosContextChanged(string value) => Recompute();

    private void Recompute()
    {
        OnPropertyChanged(nameof(UsesAgent));
        OnPropertyChanged(nameof(Problem));
        OnPropertyChanged(nameof(HasProblem));
        OnPropertyChanged(nameof(IsUsable));
    }

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
