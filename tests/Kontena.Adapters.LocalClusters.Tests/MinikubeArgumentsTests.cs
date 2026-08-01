using Kontena.Sdk.Orchestration.Provisioning;
using Xunit;

namespace Kontena.Adapters.LocalClusters.Tests;

public class MinikubeArgumentsTests
{
    [Fact]
    public void A_plain_create_names_the_profile_and_asks_for_nothing_else()
    {
        var arguments = MinikubeArguments.Create(new LocalClusterSpec("dev"));

        Assert.Equal(["start", "--profile", "dev"], arguments);
    }

    [Fact]
    public void Nodes_are_counted_the_way_minikube_counts_them()
    {
        // The spec keeps control-plane and workers apart; minikube wants the total.
        var spec = new LocalClusterSpec("dev") { ControlPlaneNodes = 1, WorkerNodes = 2 };

        var arguments = MinikubeArguments.Create(spec);

        Assert.Contains("--nodes", arguments);
        Assert.Equal("3", arguments[arguments.ToList().IndexOf("--nodes") + 1]);
    }

    [Fact]
    public void A_single_node_is_not_asked_for_at_all()
    {
        Assert.DoesNotContain("--nodes", MinikubeArguments.Create(new LocalClusterSpec("dev")));
    }

    [Fact]
    public void Resources_are_passed_in_the_units_minikube_expects()
    {
        var spec = new LocalClusterSpec("dev") { Cpus = 4, MemoryMb = 8192 };

        var arguments = MinikubeArguments.Create(spec).ToList();

        Assert.Equal("4", arguments[arguments.IndexOf("--cpus") + 1]);
        Assert.Equal("8192mb", arguments[arguments.IndexOf("--memory") + 1]);
    }

    [Theory]
    [InlineData(LocalClusterRuntime.Docker, "docker")]
    [InlineData(LocalClusterRuntime.Podman, "podman")]
    [InlineData(LocalClusterRuntime.Kvm2, "kvm2")]
    public void A_chosen_runtime_becomes_the_driver(LocalClusterRuntime runtime, string driver)
    {
        var arguments = MinikubeArguments.Create(new LocalClusterSpec("dev") { Runtime = runtime }).ToList();

        Assert.Equal(driver, arguments[arguments.IndexOf("--driver") + 1]);
    }

    [Fact]
    public void Leaving_the_runtime_alone_lets_minikube_choose()
    {
        Assert.DoesNotContain("--driver", MinikubeArguments.Create(new LocalClusterSpec("dev")));
        Assert.Null(MinikubeArguments.Driver(LocalClusterRuntime.Default));
    }

    [Fact]
    public void Every_port_mapping_gets_its_own_flag()
    {
        var spec = new LocalClusterSpec("dev")
        {
            PortMappings = [new ClusterPortMapping(8080, 80), new ClusterPortMapping(5353, 53, "udp")],
        };

        var arguments = MinikubeArguments.Create(spec).ToList();

        Assert.Equal(2, arguments.Count(a => a == "--ports"));
        Assert.Contains("8080:80/tcp", arguments);
        Assert.Contains("5353:53/udp", arguments);
    }

    [Fact]
    public void The_version_is_passed_through_untouched()
    {
        var spec = new LocalClusterSpec("dev") { KubernetesVersion = "v1.31.0" };

        var arguments = MinikubeArguments.Create(spec).ToList();

        Assert.Equal("v1.31.0", arguments[arguments.IndexOf("--kubernetes-version") + 1]);
    }

    [Fact]
    public void The_small_commands_all_name_the_profile()
    {
        Assert.Equal(["start", "--profile", "dev"], MinikubeArguments.Start("dev"));
        Assert.Equal(["stop", "--profile", "dev"], MinikubeArguments.Stop("dev"));
        Assert.Equal(["delete", "--profile", "dev"], MinikubeArguments.Delete("dev"));
        Assert.Equal(["profile", "list", "--output", "json"], MinikubeArguments.List());
    }
}
