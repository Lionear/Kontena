using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Core.Orchestration.Preflight;
using Kontena.Sdk.Orchestration.Preflight;
using Kontena.Sdk.Orchestration.Provisioning;
using Xunit;

namespace Kontena.App.Tests;

public class ProvisioningWizardViewModelTests
{
    private static FakePreflightProbe Healthy(string address) =>
        new FakePreflightProbe(address)
            .Answer("echo kontena-preflight", ProbeResult.Success("kontena-preflight"))
            .Answer("sudo -n true", ProbeResult.Success())
            .Answer("uname", ProbeResult.Success("Linux x86_64"))
            .Answer("ss -Hltn", ProbeResult.Success("LISTEN 0 128 0.0.0.0:22 0.0.0.0:*"))
            .Answer("swapon", ProbeResult.Success())
            .Answer("date +%s", ProbeResult.Success(DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()))
            .Answer("hostname", ProbeResult.Success($"node-{address[^1]}\n{Guid.NewGuid()}\naa:bb:cc:00:00:{address[^1]},"));

    private static async Task<ProvisioningWizardViewModel> WizardAsync(
        Func<RemoteClusterHost, IPreflightProbe>? probeFor = null)
    {
        var choice = new RemoteProvisionerChoiceViewModel(new FakeRemoteClusterProvisioner(), "for the test");
        var wizard = new ProvisioningWizardViewModel(
            [choice], (host, _) => (probeFor ?? (h => Healthy(h.Address)))(host));

        await wizard.LoadAsync();
        return wizard;
    }

    /// <summary>
    /// A key file to point the credentials at, so they are usable without an SSH agent.
    /// <para>
    /// Naming no key leaves <see cref="ClusterCredentialsViewModel.AgentKeys"/> to answer, and that
    /// reads <c>SSH_AUTH_SOCK</c> from the environment: no agent means "nothing to authenticate with",
    /// the credentials step never passes, and every test that walks past it fails on the machine rather
    /// than on the code. Which is what happened — green on macOS, red on the Linux and Windows runners
    /// (KON-384). The contents do not matter; only that the path exists and is not a .pub half.
    /// </para>
    /// </summary>
    private static readonly string KeyFile = WriteKeyFile();

    private static string WriteKeyFile()
    {
        // Build output, so it is cleaned with everything else and never lands in a shared temp dir.
        var path = Path.Combine(AppContext.BaseDirectory, "provisioning-wizard-tests.key");
        File.WriteAllText(path, "");
        return path;
    }

    /// <summary>Fills in everything up to and including the credentials step.</summary>
    private static void FillIn(ProvisioningWizardViewModel wizard)
    {
        wizard.Name = "prod-eu-west";

        wizard.Hosts.AddHost();
        wizard.Hosts.Hosts[0].Address = "10.0.0.1";
        wizard.Hosts.Hosts[0].Role = ClusterHostRole.Controller;

        wizard.Credentials.User = "rick";
        wizard.Credentials.KeyPath = KeyFile;
    }

    // ── The steps ────────────────────────────────────────────────────────────

    [Fact]
    public async Task It_starts_on_the_distribution_step_with_the_only_usable_one_picked()
    {
        var wizard = await WizardAsync();

        Assert.True(wizard.IsDistribution);
        Assert.True(wizard.IsFirst);
        Assert.NotNull(wizard.Selected);
        Assert.True(wizard.Selected.IsSelected);
    }

    [Fact]
    public async Task A_distribution_whose_tool_is_missing_cannot_be_picked_and_says_why()
    {
        var missing = new FakeRemoteClusterProvisioner
        {
            Readiness = new(new Kontena.Sdk.Tooling.ExternalTool("k0sctl", "k0sctl", ["version"], []),
                Kontena.Sdk.Tooling.ToolState.Missing, null, null, false, null),
        };

        var wizard = new ProvisioningWizardViewModel(
            [new RemoteProvisionerChoiceViewModel(missing, "for the test")]);

        await wizard.LoadAsync();
        wizard.Name = "prod-eu-west";

        Assert.False(wizard.CanContinue);
        Assert.Contains("not installed", wizard.Blocked, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_distribution_step_also_wants_a_name_and_uses_the_local_clusters_rule_for_it()
    {
        var wizard = await WizardAsync();

        Assert.False(wizard.CanContinue);

        wizard.Name = "Prod";
        Assert.False(wizard.CanContinue);
        Assert.Equal(LocalClusterName.Problem("Prod"), wizard.NameProblem);

        wizard.Name = "prod-eu-west";
        Assert.True(wizard.CanContinue);
    }

    [Fact]
    public async Task The_hosts_step_will_not_be_left_empty_and_says_what_it_needs()
    {
        var wizard = await WizardAsync();
        wizard.Name = "prod-eu-west";
        await wizard.NextCommand.ExecuteAsync(null);

        Assert.True(wizard.IsHosts);
        Assert.False(wizard.CanContinue);

        // The empty state's own words, not a second wording invented by the wizard.
        Assert.Equal(HostInventoryViewModel.Empty, wizard.Blocked);
    }

    [Fact]
    public async Task The_hosts_step_defers_to_the_specs_own_rule_when_something_is_wrong()
    {
        var wizard = await WizardAsync();
        wizard.Name = "prod-eu-west";
        await wizard.NextCommand.ExecuteAsync(null);

        wizard.Hosts.AddHost();
        wizard.Hosts.Hosts[0].Address = "10.0.0.1";
        wizard.Hosts.Hosts[0].Role = ClusterHostRole.Worker;

        Assert.False(wizard.CanContinue);
        Assert.Equal(wizard.Hosts.Problem, wizard.Blocked);
        Assert.Contains("controller", wizard.Blocked, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_credentials_step_defers_to_the_credential_types_own_rule()
    {
        var wizard = await WizardAsync();
        FillIn(wizard);

        await wizard.NextCommand.ExecuteAsync(null);
        await wizard.NextCommand.ExecuteAsync(null);

        Assert.True(wizard.IsCredentials);
        Assert.Equal(wizard.Credentials.Problem, wizard.Blocked);
    }

    [Fact]
    public async Task Back_walks_the_steps_in_reverse_and_stops_at_the_first()
    {
        var wizard = await WizardAsync();
        FillIn(wizard);

        await wizard.NextCommand.ExecuteAsync(null);
        Assert.True(wizard.IsHosts);

        wizard.BackCommand.Execute(null);
        Assert.True(wizard.IsDistribution);

        wizard.BackCommand.Execute(null);
        Assert.True(wizard.IsDistribution);
    }

    // ── The steps are the existing view models, not copies ───────────────────

    [Fact]
    public async Task The_hosts_step_is_the_host_inventory_from_KON_233()
    {
        var wizard = await WizardAsync();

        Assert.IsType<HostInventoryViewModel>(wizard.Hosts);
        Assert.True(wizard.Hosts.IsEmpty);
    }

    [Fact]
    public async Task The_credentials_form_follows_the_chosen_transport()
    {
        var wizard = await WizardAsync();

        Assert.Equal(ProvisionerTransport.Ssh, wizard.Credentials.Transport);
        Assert.True(wizard.Credentials.IsSsh);
    }

    [Fact]
    public async Task Choosing_a_talos_style_distribution_replaces_the_credentials_form()
    {
        var talos = new FakeRemoteClusterProvisioner
        {
            Provisioner = "talos",
            Capabilities = new ProvisionerCapabilities
            {
                NeedsHosts = true,
                Transport = ProvisionerTransport.MachineApi,
            },
        };

        var wizard = new ProvisioningWizardViewModel([
            new RemoteProvisionerChoiceViewModel(new FakeRemoteClusterProvisioner(), "ssh one"),
            new RemoteProvisionerChoiceViewModel(talos, "machine api one"),
        ]);

        await wizard.LoadAsync();

        wizard.Credentials.KeyPath = "/keys/for-k0s";
        var before = wizard.Credentials;

        wizard.SelectProvisionerCommand.Execute(wizard.Provisioners[1]);

        // Replaced, not edited: a key path typed for one transport must not reappear under the other.
        Assert.NotSame(before, wizard.Credentials);
        Assert.True(wizard.Credentials.IsTalos);
        Assert.Empty(wizard.Credentials.KeyPath);
    }

    // ── Preflight drives the button ──────────────────────────────────────────

    [Fact]
    public async Task Arriving_at_the_preflight_runs_it_rather_than_waiting_for_a_second_button()
    {
        var wizard = await WizardAsync();
        FillIn(wizard);

        await wizard.NextCommand.ExecuteAsync(null);
        await wizard.NextCommand.ExecuteAsync(null);
        await wizard.NextCommand.ExecuteAsync(null);

        Assert.True(wizard.IsPreflight);
        Assert.True(wizard.Preflight.HasRun);
        Assert.True(wizard.CanContinue);
    }

    [Fact]
    public async Task A_machine_that_fails_a_blocking_check_stops_the_wizard()
    {
        var wizard = await WizardAsync(host =>
            Healthy(host.Address).Answer("swapon", ProbeResult.Success("/dev/dm-1")));

        FillIn(wizard);

        for (var i = 0; i < 3; i++)
            await wizard.NextCommand.ExecuteAsync(null);

        Assert.False(wizard.CanContinue);
        Assert.Equal(wizard.Preflight.Report?.Summary, wizard.Blocked);
    }

    [Fact]
    public async Task The_wizards_verdict_is_the_reports_verdict_and_not_a_second_opinion()
    {
        var wizard = await WizardAsync(host =>
            Healthy(host.Address).Answer("ss -Hltn", ProbeResult.Exit(127)));

        FillIn(wizard);

        for (var i = 0; i < 3; i++)
            await wizard.NextCommand.ExecuteAsync(null);

        // "Could not be checked" on a blocking check stops it, exactly as the engine decided.
        Assert.False(wizard.Preflight.CanContinue);
        Assert.Equal(wizard.Preflight.CanContinue, wizard.CanContinue);
    }

    [Fact]
    public async Task Cluster_wide_findings_get_their_own_group_rather_than_being_blamed_on_a_machine()
    {
        var wizard = await WizardAsync(host =>
            Healthy(host.Address).Answer("hostname", ProbeResult.Success($"same\n{Guid.NewGuid()}\naa:bb:cc:00:00:01,")));

        wizard.Name = "prod-eu-west";
        wizard.Credentials.User = "rick";
        wizard.Credentials.KeyPath = KeyFile;

        foreach (var address in new[] { "10.0.0.1", "10.0.0.2" })
        {
            wizard.Hosts.AddHost();
            wizard.Hosts.Hosts[^1].Address = address;
            wizard.Hosts.Hosts[^1].Role = ClusterHostRole.Controller;
        }

        for (var i = 0; i < 3; i++)
            await wizard.NextCommand.ExecuteAsync(null);

        var cluster = Assert.Single(wizard.Preflight.Groups.Where(g => g.IsCluster));

        Assert.Equal("Across the cluster", cluster.Title);
        Assert.Contains(cluster.Rows, r => r.Reason.Contains("cloning a VM", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Changing_the_machines_throws_away_what_was_checked_about_the_old_ones()
    {
        var wizard = await WizardAsync();
        FillIn(wizard);

        for (var i = 0; i < 3; i++)
            await wizard.NextCommand.ExecuteAsync(null);

        Assert.True(wizard.Preflight.HasRun);

        wizard.Cni = "calico";

        // Calico adds BGP on 179 to what is checked, so the old answer is about a different question.
        Assert.False(wizard.Preflight.HasRun);
        Assert.False(wizard.CanContinue);
    }

    [Fact]
    public async Task A_remedy_runs_and_the_row_shows_whatever_the_re_check_said()
    {
        var probe = Healthy("10.0.0.1").Answer("swapon", ProbeResult.Success("/dev/dm-1"));
        var wizard = await WizardAsync(_ => probe);

        FillIn(wizard);

        for (var i = 0; i < 3; i++)
            await wizard.NextCommand.ExecuteAsync(null);

        var swap = wizard.Preflight.Groups.SelectMany(g => g.Rows).Single(r => r.HasRemedy);
        Assert.True(swap.IsFailed);

        // The machine now reports no swap, as it would after swapoff.
        probe.Answer("swapon", ProbeResult.Success());
        await swap.ApplyCommand.ExecuteAsync(null);

        Assert.True(swap.IsPassed);
        Assert.True(wizard.CanContinue);
    }

    // ── What it hands on ─────────────────────────────────────────────────────

    [Fact]
    public async Task It_builds_the_spec_KON_239_will_take()
    {
        var wizard = await WizardAsync();
        FillIn(wizard);
        wizard.Cni = "calico";

        var spec = wizard.Build();

        Assert.NotNull(spec);
        Assert.Equal("prod-eu-west", spec.Name);
        Assert.Equal("calico", spec.Cni);
        Assert.Null(spec.Problem());
    }

    [Fact]
    public async Task An_incomplete_wizard_builds_nothing_rather_than_half_a_spec()
    {
        var wizard = await WizardAsync();

        Assert.Null(wizard.Build());
    }

    [Fact]
    public async Task Continuing_from_the_preflight_starts_the_rollout()
    {
        var wizard = await WizardAsync();
        FillIn(wizard);

        for (var i = 0; i < 4; i++)
            await wizard.NextCommand.ExecuteAsync(null);

        Assert.True(wizard.IsRollout);
        Assert.True(wizard.IsLast);
        Assert.NotNull(wizard.Rollout);
        Assert.True(wizard.Rollout.IsDone);
    }

    [Fact]
    public async Task There_is_no_way_back_out_of_a_rollout()
    {
        var wizard = await WizardAsync();
        FillIn(wizard);

        for (var i = 0; i < 4; i++)
            await wizard.NextCommand.ExecuteAsync(null);

        // The earlier steps describe a cluster that is now partly real; editing them would be editing
        // a plan that has already been acted on.
        Assert.False(wizard.CanGoBack);

        wizard.BackCommand.Execute(null);
        Assert.True(wizard.IsRollout);
    }
}
