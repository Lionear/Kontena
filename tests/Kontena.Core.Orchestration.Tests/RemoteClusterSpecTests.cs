using Kontena.Sdk.Orchestration.Provisioning;
using Xunit;

namespace Kontena.Core.Orchestration.Tests;

public class RemoteClusterSpecTests
{
    private static RemoteClusterSpec Spec(params ClusterHostRole[] roles) =>
        new("dev", [.. roles.Select((role, i) => new RemoteClusterHost($"10.0.0.{i + 1}", role))]);

    private const ClusterHostRole Controller = ClusterHostRole.Controller;
    private const ClusterHostRole Worker = ClusterHostRole.Worker;

    [Fact]
    public void A_controller_and_two_workers_is_usable()
    {
        var spec = Spec(Controller, Worker, Worker);

        Assert.Null(spec.Problem());
        Assert.True(spec.IsValid());
        Assert.Empty(spec.Warnings());
        Assert.Equal(1, spec.ControllerCount);
    }

    [Fact]
    public void A_cluster_without_machines_is_refused()
    {
        Assert.NotNull(new RemoteClusterSpec("dev", []).Problem());
    }

    [Fact]
    public void A_cluster_of_workers_alone_is_refused_and_says_why()
    {
        Assert.Contains("controller", Spec(Worker, Worker).Problem(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_cluster_name_follows_the_same_rules_as_a_local_one()
    {
        var spec = Spec(Controller) with { Name = "Dev" };

        Assert.Equal(LocalClusterName.Problem("Dev"), spec.Problem());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("node 1")]
    [InlineData("ssh://node-1")]
    public void A_host_that_is_neither_an_address_nor_a_hostname_is_refused(string address)
    {
        var spec = new RemoteClusterSpec("dev", [new RemoteClusterHost(address, Controller)]);

        Assert.NotNull(spec.Problem());
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("node-1.lan")]
    [InlineData("fd00::1")]
    public void An_ip_or_a_hostname_is_accepted(string address)
    {
        var spec = new RemoteClusterSpec("dev", [new RemoteClusterHost(address, Controller)]);

        Assert.Null(spec.Problem());
    }

    [Fact]
    public void The_same_machine_twice_is_refused_whatever_its_casing()
    {
        var spec = new RemoteClusterSpec("dev", [
            new RemoteClusterHost("Node-1.lan", Controller),
            new RemoteClusterHost("node-1.lan", Worker),
        ]);

        Assert.Contains("twice", spec.Problem(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("10.244.0.0")]
    [InlineData("10.244.0.0/33")]
    [InlineData("not-a-network")]
    public void A_pod_network_that_is_not_a_cidr_is_refused(string cidr)
    {
        Assert.NotNull((Spec(Controller) with { PodCidr = cidr }).Problem());
    }

    [Fact]
    public void A_service_network_that_is_not_a_cidr_is_refused()
    {
        Assert.NotNull((Spec(Controller) with { ServiceCidr = "10.96.0.0" }).Problem());
    }

    [Fact]
    public void Networks_left_out_are_the_provisioners_own_business()
    {
        var spec = Spec(Controller) with { PodCidr = "10.244.0.0/16", ServiceCidr = "10.96.0.0/12" };

        Assert.Null(spec.Problem());
    }

    [Fact]
    public void Two_controllers_are_allowed_through_with_a_warning_about_quorum()
    {
        var spec = Spec(Controller, Controller) with { ControlPlaneEndpoint = "api.lan:6443" };

        Assert.Null(spec.Problem());
        Assert.True(spec.IsValid());

        var warning = Assert.Single(spec.Warnings());
        Assert.Contains("quorum", warning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void An_odd_number_of_controllers_draws_no_quorum_warning(int controllers)
    {
        var spec = Spec([.. Enumerable.Repeat(Controller, controllers)])
            with { ControlPlaneEndpoint = "api.lan:6443" };

        Assert.DoesNotContain(spec.Warnings(), w => w.Contains("quorum", StringComparison.Ordinal));
    }

    [Fact]
    public void Four_controllers_draw_the_same_warning_as_two()
    {
        var spec = Spec(Controller, Controller, Controller, Controller)
            with { ControlPlaneEndpoint = "api.lan:6443" };

        Assert.Contains("quorum", Assert.Single(spec.Warnings()), StringComparison.Ordinal);
    }

    [Fact]
    public void Several_controllers_without_an_endpoint_are_warned_about_the_first_one()
    {
        var spec = Spec(Controller, Controller, Controller);

        Assert.Null(spec.Problem());
        Assert.Contains(
            spec.Warnings(),
            w => w.Contains("control-plane endpoint", StringComparison.Ordinal));
    }

    [Fact]
    public void One_controller_needs_no_endpoint()
    {
        Assert.Empty(Spec(Controller, Worker).Warnings());
    }
}
