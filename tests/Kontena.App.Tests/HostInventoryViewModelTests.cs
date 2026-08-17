using Kontena.App.ViewModels;
using Kontena.Sdk.Orchestration.Provisioning;
using Xunit;

namespace Kontena.App.Tests;

public class HostInventoryViewModelTests
{
    private static HostInventoryViewModel WithHosts(params (string Address, ClusterHostRole Role)[] hosts)
    {
        var vm = new HostInventoryViewModel();

        foreach (var (address, role) in hosts)
        {
            vm.AddHost();
            vm.Hosts[^1].Address = address;
            vm.Hosts[^1].Role = role;
        }

        return vm;
    }

    private const ClusterHostRole Controller = ClusterHostRole.Controller;
    private const ClusterHostRole Worker = ClusterHostRole.Worker;

    [Fact]
    public void An_empty_table_asks_for_a_controller_rather_than_reporting_no_hosts()
    {
        var vm = new HostInventoryViewModel();

        Assert.True(vm.IsEmpty);
        Assert.Contains("controller", vm.EmptyMessage, StringComparison.Ordinal);

        // The blocking problem stays quiet while the empty state is doing the talking.
        Assert.False(vm.HasProblem);
    }

    [Fact]
    public void The_first_machine_added_is_a_controller_because_that_is_what_was_just_asked_for()
    {
        var vm = new HostInventoryViewModel();
        vm.AddHost();

        Assert.Equal(Controller, vm.Hosts[0].Role);
        Assert.False(vm.IsEmpty);
    }

    [Fact]
    public void Later_machines_default_to_worker()
    {
        var vm = new HostInventoryViewModel();
        vm.AddHost();
        vm.AddHost();

        Assert.Equal(Worker, vm.Hosts[1].Role);
    }

    [Fact]
    public void Removing_the_last_machine_brings_the_empty_state_back()
    {
        var vm = WithHosts(("10.0.0.1", Controller));
        vm.Hosts[0].RemoveCommand.Execute(null);

        Assert.True(vm.IsEmpty);
        Assert.Empty(vm.Build());
    }

    [Fact]
    public void Editing_a_row_is_reflected_in_the_counts()
    {
        var vm = WithHosts(("10.0.0.1", Controller), ("10.0.0.2", Worker));

        Assert.Equal(1, vm.ControllerCount);
        Assert.Equal(1, vm.WorkerCount);

        vm.Hosts[1].Role = Controller;

        Assert.Equal(2, vm.ControllerCount);
        Assert.Equal(0, vm.WorkerCount);
    }

    [Fact]
    public void The_summary_counts_what_is_there_and_gets_its_plurals_right()
    {
        Assert.Equal("1 controller · 0 workers", WithHosts(("10.0.0.1", Controller)).Summary);
        Assert.Equal(
            "1 controller · 2 workers",
            WithHosts(("10.0.0.1", Controller), ("10.0.0.2", Worker), ("10.0.0.3", Worker)).Summary);
    }

    [Fact]
    public void A_row_with_no_address_is_not_counted_yet()
    {
        var vm = new HostInventoryViewModel();
        vm.AddHost();

        Assert.Equal(0, vm.ControllerCount);
        Assert.Empty(vm.Build());
        Assert.True(vm.Hosts[0].IsEmpty);
    }

    [Fact]
    public void Two_controllers_warn_about_quorum_without_blocking()
    {
        var vm = WithHosts(("10.0.0.1", Controller), ("10.0.0.2", Controller));

        Assert.True(vm.HasWarning);
        Assert.Contains("quorum", vm.Warning, StringComparison.Ordinal);
        Assert.False(vm.HasProblem);
    }

    [Fact]
    public void The_warning_is_the_one_the_spec_already_words()
    {
        var vm = WithHosts(("10.0.0.1", Controller), ("10.0.0.2", Controller));

        Assert.Equal(RemoteClusterSpec.QuorumWarning(2), vm.Warning);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void An_odd_number_of_controllers_is_quiet(int controllers)
    {
        var vm = WithHosts([.. Enumerable.Range(1, controllers).Select(i => ($"10.0.0.{i}", Controller))]);

        Assert.False(vm.HasWarning);
    }

    [Fact]
    public void Workers_alone_are_blocked_and_the_reason_names_the_control_plane()
    {
        var vm = WithHosts(("10.0.0.1", Worker));

        Assert.True(vm.HasProblem);
        Assert.Contains("controller", vm.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_address_twice_is_caught_by_the_specs_own_rule()
    {
        var vm = WithHosts(("10.0.0.1", Controller), ("10.0.0.1", Worker));

        Assert.Equal(RemoteClusterSpec.HostsProblem(vm.Build()), vm.Problem);
        Assert.Contains("twice", vm.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Importing_a_k0sctl_file_fills_the_table()
    {
        var vm = new HostInventoryViewModel();

        vm.ImportK0sctl("""
            spec:
              hosts:
                - ssh: {address: 10.10.4.11, user: rick, keyPath: ~/.ssh/id_ed25519}
                  role: controller
                - ssh: {address: 10.10.4.21, user: rick}
                  role: worker
            """);

        Assert.Equal(2, vm.Hosts.Count);
        Assert.Equal("10.10.4.11", vm.Hosts[0].Address);
        Assert.Equal("rick", vm.Hosts[0].User);
        Assert.Equal("~/.ssh/id_ed25519", vm.Hosts[0].KeyPath);
        Assert.Equal(Worker, vm.Hosts[1].Role);
        Assert.Equal("1 controller · 1 worker", vm.Summary);
    }

    [Fact]
    public void An_import_adds_to_what_is_there_instead_of_throwing_it_away()
    {
        var vm = WithHosts(("10.0.0.9", Controller));

        vm.ImportK0sctl("""
            spec:
              hosts:
                - ssh: {address: 10.10.4.21}
                  role: worker
            """);

        Assert.Equal(2, vm.Hosts.Count);
        Assert.Equal("10.0.0.9", vm.Hosts[0].Address);
    }

    [Fact]
    public void A_machine_already_in_the_table_is_skipped_and_said_so()
    {
        var vm = WithHosts(("10.10.4.11", Controller));

        vm.ImportK0sctl("""
            spec:
              hosts:
                - ssh: {address: 10.10.4.11}
                  role: controller
                - ssh: {address: 10.10.4.21}
                  role: worker
            """);

        Assert.Equal(2, vm.Hosts.Count);
        Assert.Contains("skipped 1", vm.ImportMessage, StringComparison.Ordinal);

        // And the table is not left holding a duplicate the validation would flag.
        Assert.False(vm.HasProblem);
    }

    [Fact]
    public void A_file_with_nothing_in_it_says_what_was_expected()
    {
        var vm = new HostInventoryViewModel();
        vm.ImportK0sctl("nothing: useful");

        Assert.Empty(vm.Hosts);
        Assert.Contains("k0sctl.yaml", vm.ImportMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void What_the_table_builds_is_what_the_spec_takes()
    {
        var vm = WithHosts(("10.0.0.1", Controller), ("10.0.0.2", Worker));
        vm.Hosts[0].NodeName = "cp-1";

        var spec = new RemoteClusterSpec("prod", vm.Build());

        Assert.Null(spec.Problem());
        Assert.Equal("cp-1", spec.Hosts[0].NodeName);
    }
}
