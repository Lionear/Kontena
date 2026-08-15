using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.Sdk.Orchestration.Provisioning;
using Xunit;

namespace Kontena.App.Tests;

public class ClusterCredentialsViewModelTests
{
    private static ClusterCredentialsViewModel Ssh(IReadOnlyCollection<string>? agentKeys = null) =>
        new(ProvisionerTransport.Ssh) { AgentKeys = agentKeys };

    [Fact]
    public void The_ssh_transport_shows_the_ssh_form_and_nothing_talos()
    {
        var vm = Ssh();

        Assert.True(vm.IsSsh);
        Assert.False(vm.IsTalos);
        Assert.False(vm.NeedsNothing);
    }

    [Fact]
    public void The_machine_api_transport_shows_talos_and_no_ssh_fields()
    {
        var vm = new ClusterCredentialsViewModel(ProvisionerTransport.MachineApi);

        Assert.True(vm.IsTalos);
        Assert.False(vm.IsSsh);
    }

    [Fact]
    public void A_local_provisioner_is_asked_for_nothing_and_builds_nothing()
    {
        var vm = new ClusterCredentialsViewModel(ProvisionerTransport.Local);

        Assert.True(vm.NeedsNothing);
        Assert.Null(vm.Build());
        Assert.False(vm.HasProblem);
    }

    [Fact]
    public void The_form_follows_the_transport_rather_than_a_distribution_name()
    {
        // kubeadm and k0s differ in nearly everything else and are identical here — which is the whole
        // point of switching on the capability instead of on which tool was picked.
        foreach (var transport in new[] { ProvisionerTransport.Ssh, ProvisionerTransport.MachineApi })
        {
            var vm = new ClusterCredentialsViewModel(transport);
            Assert.Equal(transport, vm.Build()?.Transport);
        }
    }

    [Fact]
    public void An_empty_key_field_means_the_agent_answers()
    {
        var vm = Ssh(["id_ed25519"]);
        vm.User = "rick";

        Assert.True(vm.UsesAgent);
        Assert.True(vm.IsUsable);
    }

    [Fact]
    public void An_agent_with_nothing_in_it_blocks_and_says_what_to_run()
    {
        var vm = Ssh([]);
        vm.User = "rick";

        Assert.True(vm.HasProblem);
        Assert.Contains("ssh-add", vm.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Naming_a_key_stops_the_agent_being_the_question()
    {
        var vm = Ssh([]);
        vm.User = "rick";
        vm.KeyPath = "/definitely/not/here";

        Assert.False(vm.UsesAgent);
        Assert.DoesNotContain("ssh-add", vm.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_path_that_does_not_exist_is_reported_as_it_is_typed()
    {
        var vm = Ssh(["id_ed25519"]);
        vm.KeyPath = "/definitely/not/here";

        Assert.True(vm.HasProblem);
        Assert.Contains("/definitely/not/here", vm.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void The_problem_is_the_credential_types_own_wording()
    {
        var vm = Ssh([]);
        vm.User = "rick";

        Assert.Equal(((SshCredentials)vm.Build()!).Problem([]), vm.Problem);
    }

    [Fact]
    public void Talos_without_a_config_blocks()
    {
        var vm = new ClusterCredentialsViewModel(ProvisionerTransport.MachineApi);

        Assert.True(vm.HasProblem);
        Assert.Contains("talosconfig", vm.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void What_the_ssh_form_builds_carries_the_fields_across()
    {
        var vm = Ssh(["id_ed25519"]);
        vm.User = " rick ";
        vm.UseSudo = false;

        var built = Assert.IsType<SshCredentials>(vm.Build());

        Assert.Equal("rick", built.User);
        Assert.Null(built.KeyPath);
        Assert.False(built.UseSudo);
    }

    [Fact]
    public void What_the_talos_form_builds_carries_the_context_across()
    {
        var vm = new ClusterCredentialsViewModel(ProvisionerTransport.MachineApi)
        {
            TalosConfigPath = "/home/rick/talosconfig",
            TalosContext = "prod-eu-west",
        };

        var built = Assert.IsType<TalosCredentials>(vm.Build());

        Assert.Equal("/home/rick/talosconfig", built.ConfigPath);
        Assert.Equal("prod-eu-west", built.Context);
    }

    [Fact]
    public void There_is_no_password_field_to_bind_to()
    {
        Assert.DoesNotContain(
            typeof(ClusterCredentialsViewModel).GetProperties(),
            p => p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_cluster_gets_its_own_keychain_entry_named_the_way_the_others_are()
    {
        var key = SecretKeys.Cluster("Prod-EU-West");

        Assert.Equal("kontena:cluster:prod-eu-west", key);
        Assert.Contains("prod-eu-west", SecretKeys.Describe(key), StringComparison.Ordinal);
    }
}
