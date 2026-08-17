using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Orchestration.Preflight;
using Kontena.Core.Orchestration.Provisioning;
using Kontena.Sdk.Orchestration.Provisioning;

namespace Kontena.App.ViewModels;

/// <summary>Where the provisioning wizard is.</summary>
public enum ProvisioningStep
{
    /// <summary>Which distribution builds this cluster, and what it is called.</summary>
    Distribution,

    /// <summary>Which machines take part, and what each one is for.</summary>
    Hosts,

    /// <summary>How the rollout gets in.</summary>
    Credentials,

    /// <summary>Whether the machines can actually take it.</summary>
    Preflight,

    /// <summary>Installing, and whatever is left on screen if it stops (KON-239).</summary>
    Rollout,
}

/// <summary>
/// The screen that ties the provisioning pieces together (KON-379).
/// <para>
/// It builds nothing of its own. The host table is KON-233's view model, the credentials form is
/// KON-234's, the preflight is KON-235's engine behind a step, and the distributions are KON-236's
/// provisioners. What was missing was a place to put them and a rule for moving between them, and
/// that is all this is: five view models, an index, and one question per step about whether it may be
/// left.
/// </para>
/// <para>
/// Every one of those questions is answered by the piece that owns it —
/// <see cref="RemoteClusterSpec.HostsProblem"/>, <see cref="ClusterCredentialsViewModel.IsUsable"/>,
/// <c>PreflightReport.CanContinue</c>. A wizard that re-derived any of them would be a second opinion
/// that drifts.
/// </para>
/// </summary>
public sealed partial class ProvisioningWizardViewModel : ViewModelBase
{
    private readonly Func<RemoteClusterHost, SshCredentials, IPreflightProbe> _probeFor;
    private readonly RolloutRecordStore _records;

    /// <param name="provisioners">The distributions to offer, already checked for their tooling.</param>
    /// <param name="probeFor">
    /// How to reach a machine for the preflight. Injected so a demo can hand over a fake and the
    /// screen works with no machines at all — which is what KON-236's fakes were built for.
    /// </param>
    /// <param name="records">Where an interrupted rollout is remembered. Injected for the same reason.</param>
    public ProvisioningWizardViewModel(
        IReadOnlyList<RemoteProvisionerChoiceViewModel> provisioners,
        Func<RemoteClusterHost, SshCredentials, IPreflightProbe>? probeFor = null,
        RolloutRecordStore? records = null)
    {
        ArgumentNullException.ThrowIfNull(provisioners);

        _probeFor = probeFor ?? ((host, credentials) => new SshPreflightProbe(host, credentials));
        _records = records ?? new RolloutRecordStore();

        Provisioners = [.. provisioners];
        Preflight = new PreflightStepViewModel(host => _probeFor(host, CredentialsForProbe()));

        // Before Selected, because setting that rebuilds it — and the rebuild reads this field.
        Credentials = BuildCredentials();

        Selected = Provisioners.FirstOrDefault(p => p.IsUsable) ?? Provisioners.FirstOrDefault();

        Hosts.PropertyChanged += (_, _) => OnStepStateChanged();
    }

    public ObservableCollection<RemoteProvisionerChoiceViewModel> Provisioners { get; }

    [ObservableProperty] private RemoteProvisionerChoiceViewModel? _selected;

    /// <summary>The host table from KON-233, used as-is.</summary>
    public HostInventoryViewModel Hosts { get; } = new();

    /// <summary>The credentials form from KON-234. Replaced when the transport changes, never edited.</summary>
    [ObservableProperty] private ClusterCredentialsViewModel _credentials = null!;

    /// <summary>The preflight from KON-235, behind a step.</summary>
    public PreflightStepViewModel Preflight { get; }

    /// <summary>
    /// The rollout from KON-239, or null while no distribution is chosen. Rebuilt with the
    /// distribution, since it is that provisioner that does the installing.
    /// </summary>
    [ObservableProperty] private RolloutViewModel? _rollout;

    [ObservableProperty] private ProvisioningStep _step = ProvisioningStep.Distribution;

    /// <summary>
    /// What the cluster is called. Asked here rather than in a later configuration step, which is
    /// where the mockup puts it: the name is in the kubeconfig context, the preflight report and the
    /// k0sctl document, so a wizard that collects it last collects it after three screens have needed it.
    /// </summary>
    [ObservableProperty] private string _name = string.Empty;

    /// <summary>Which CNI to install, or empty for the distribution's own. Only where it is a choice.</summary>
    [ObservableProperty] private string _cni = string.Empty;

    public bool ShowCni => Selected?.Capabilities.ChoosesCni == true;

    /// <summary>What is wrong with the name, or null. Same rule as a local cluster's — it is a context.</summary>
    public string? NameProblem => Name.Length == 0 ? null : LocalClusterName.Problem(Name);

    public bool HasNameProblem => NameProblem is not null;

    // ── Where we are ─────────────────────────────────────────────────────────

    public bool IsDistribution => Step == ProvisioningStep.Distribution;
    public bool IsHosts => Step == ProvisioningStep.Hosts;
    public bool IsCredentials => Step == ProvisioningStep.Credentials;
    public bool IsPreflight => Step == ProvisioningStep.Preflight;
    public bool IsRollout => Step == ProvisioningStep.Rollout;

    public bool IsFirst => Step == ProvisioningStep.Distribution;

    /// <summary>The rollout is the end: there is nowhere forward from installing.</summary>
    public bool IsLast => Step == ProvisioningStep.Rollout;

    /// <summary>
    /// No going back out of a rollout. Once machines have been written to, the earlier steps describe
    /// a cluster that is already partly real, and editing them would be editing a plan that has been
    /// acted on.
    /// </summary>
    public bool CanGoBack => !IsFirst && !IsRollout;

    /// <summary>The heading, so the shell does not carry a switch of its own.</summary>
    public string Title => Step switch
    {
        ProvisioningStep.Distribution => "What builds this cluster?",
        ProvisioningStep.Hosts => "Which machines take part?",
        ProvisioningStep.Credentials => "How does the rollout get in?",
        ProvisioningStep.Preflight => "Can these machines take it?",
        _ => "Rolling out",
    };

    /// <summary>What the forward button says. "Check machines" on the step before the check.</summary>
    public string NextLabel => Step switch
    {
        ProvisioningStep.Credentials => "Check machines",
        ProvisioningStep.Preflight => "Roll out",
        _ => "Continue",
    };

    // ── Whether this step may be left ────────────────────────────────────────

    /// <summary>
    /// One question per step, each answered by whoever owns the rule. Nothing here re-derives a
    /// verdict that already exists somewhere better.
    /// </summary>
    public bool CanContinue => Step switch
    {
        ProvisioningStep.Distribution => Selected is { IsUsable: true } && LocalClusterName.IsValid(Name),
        ProvisioningStep.Hosts => !Hosts.IsEmpty && !Hosts.HasProblem,
        ProvisioningStep.Credentials => Credentials.IsUsable,

        // KON-235's single value, and deliberately not "no blockers" recomputed here.
        ProvisioningStep.Preflight => Preflight.CanContinue && Build() is not null,

        // Nowhere forward from a rollout.
        _ => false,
    };

    /// <summary>
    /// Why the forward button is off, or null when it is not. A disabled button with no reason is the
    /// dead end this epic has been avoiding since KON-232.
    /// </summary>
    public string? Blocked => Step switch
    {
        _ when CanContinue => null,
        ProvisioningStep.Distribution when Selected is null => "No distribution is available.",
        ProvisioningStep.Distribution when Selected is { IsUsable: false } =>
            $"{Selected.Name} needs its tool installed first — it is {Selected.State}.",
        ProvisioningStep.Distribution => NameProblem ?? "Give the cluster a name.",
        ProvisioningStep.Hosts when Hosts.IsEmpty => HostInventoryViewModel.Empty,
        ProvisioningStep.Hosts => Hosts.Problem,
        ProvisioningStep.Credentials => Credentials.Problem,
        ProvisioningStep.Rollout => null,
        _ when !Preflight.HasRun => "Run the checks first.",
        _ => Preflight.Report?.Summary,
    };

    public bool IsBlocked => Blocked is not null;

    // ── Moving ───────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task Next()
    {
        if (!CanContinue || IsLast)
            return;

        Step = Step + 1;

        // Arriving at the preflight runs it: the step exists to answer a question, and making someone
        // press a second button to ask it is the dead-button mistake in a hat.
        if (IsPreflight && !Preflight.HasRun)
            await RunPreflightAsync();

        // Arriving at the rollout starts it. Unlike the preflight this writes to machines, so the
        // press that got here is the consent — which is why "Roll out" is what that button says.
        if (IsRollout)
            await StartRolloutAsync();
    }

    /// <summary>Installs what the wizard has described. Public so a resumed rollout can re-enter it.</summary>
    public async Task StartRolloutAsync(CancellationToken ct = default)
    {
        if (Rollout is not { } rollout || Build() is not { } spec || Credentials.Build() is not { } credentials)
            return;

        await rollout.RunAsync(spec, credentials, ct);
        OnStepStateChanged();
    }

    [RelayCommand]
    private void Back()
    {
        if (CanGoBack)
            Step = Step - 1;
    }

    /// <summary>Runs the checks again — after fixing something, which is the loop that step is for.</summary>
    [RelayCommand]
    public async Task RunPreflightAsync()
    {
        await Preflight.RunAsync(Hosts.Build(), Blank(Cni));
        OnStepStateChanged();
    }

    [RelayCommand]
    private void SelectProvisioner(RemoteProvisionerChoiceViewModel? choice)
    {
        if (choice is { IsUsable: true })
            Selected = choice;
    }

    /// <summary>
    /// Asks each distribution's tool whether it is here. Awaited by the page rather than the
    /// constructor: locating a tool means running one, and a form that blocks on that while being
    /// built is a form that hangs before it has drawn.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        foreach (var choice in Provisioners)
            await choice.RefreshAsync(ct);

        // The first usable one, now that we know which those are.
        Selected = Provisioners.FirstOrDefault(p => p.IsUsable) ?? Provisioners.FirstOrDefault();
        OnStepStateChanged();
    }

    /// <summary>
    /// The spec this wizard has described, or null while it is not complete. What KON-239 will take.
    /// </summary>
    public RemoteClusterSpec? Build()
    {
        if (!LocalClusterName.IsValid(Name) || Hosts.IsEmpty || Hosts.HasProblem)
            return null;

        return new RemoteClusterSpec(Name.Trim(), Hosts.Build())
        {
            Cni = ShowCni ? Blank(Cni) : null,
        };
    }

    private ClusterCredentialsViewModel BuildCredentials()
    {
        var credentials = new ClusterCredentialsViewModel(Selected?.Transport ?? ProvisionerTransport.Local);
        credentials.PropertyChanged += (_, _) => OnStepStateChanged();

        return credentials;
    }

    /// <summary>
    /// What the probe logs in with. SSH only — the preflight checks assume a shell, which is exactly
    /// what Talos does not have, and a machine-API preflight is its own set of checks (KON-235).
    /// </summary>
    private SshCredentials CredentialsForProbe() =>
        Credentials.Build() as SshCredentials ?? new SshCredentials();

    partial void OnSelectedChanged(RemoteProvisionerChoiceViewModel? value)
    {
        foreach (var choice in Provisioners)
            choice.IsSelected = ReferenceEquals(choice, value);

        // A new transport is a different form, not the same form with other fields — so it is replaced
        // rather than edited. A key path typed for k0s must not reappear as a talosconfig.
        Credentials = BuildCredentials();

        // What was checked was checked with the old distribution's ports and CNI in mind.
        Preflight.Clear();

        // It is the provisioner that installs, so the rollout belongs to whichever one is chosen.
        Rollout = value is null ? null : new RolloutViewModel(value.Provisioner, _records);

        OnPropertyChanged(nameof(ShowCni));
        OnStepStateChanged();
    }

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(NameProblem));
        OnPropertyChanged(nameof(HasNameProblem));
        OnStepStateChanged();
    }

    partial void OnCniChanged(string value)
    {
        // The ports the preflight looks at depend on it — Calico adds BGP on 179.
        Preflight.Clear();
        OnStepStateChanged();
    }

    partial void OnStepChanged(ProvisioningStep value)
    {
        foreach (var name in new[]
                 {
                     nameof(IsDistribution), nameof(IsHosts), nameof(IsCredentials), nameof(IsPreflight),
                     nameof(IsRollout), nameof(IsFirst), nameof(IsLast), nameof(CanGoBack), nameof(Title),
                     nameof(NextLabel),
                 })
        {
            OnPropertyChanged(name);
        }

        OnStepStateChanged();
    }

    private void OnStepStateChanged()
    {
        OnPropertyChanged(nameof(CanContinue));
        OnPropertyChanged(nameof(Blocked));
        OnPropertyChanged(nameof(IsBlocked));
    }

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
