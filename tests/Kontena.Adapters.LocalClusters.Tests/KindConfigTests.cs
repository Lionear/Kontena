using Kontena.Core.Orchestration.Provisioning;
using Xunit;

namespace Kontena.Adapters.LocalClusters.Tests;

public class KindConfigTests
{
    [Fact]
    public void A_single_node_cluster_needs_no_config_at_all()
    {
        Assert.False(KindConfig.Needed(new LocalClusterSpec("dev")));
    }

    [Theory]
    [InlineData(2, 0, false)]
    [InlineData(1, 1, false)]
    [InlineData(1, 0, true)]
    public void Anything_the_flags_cannot_express_needs_one(int controlPlane, int workers, bool ingress)
    {
        var spec = new LocalClusterSpec("dev")
        {
            ControlPlaneNodes = controlPlane,
            WorkerNodes = workers,
            IngressReady = ingress,
        };

        Assert.True(KindConfig.Needed(spec));
    }

    [Fact]
    public void Port_mappings_alone_need_one()
    {
        var spec = new LocalClusterSpec("dev") { PortMappings = [new ClusterPortMapping(8080, 80)] };

        Assert.True(KindConfig.Needed(spec));
    }

    [Fact]
    public void Nodes_are_written_control_plane_first()
    {
        var spec = new LocalClusterSpec("dev") { ControlPlaneNodes = 3, WorkerNodes = 2 };

        var lines = Roles(KindConfig.Write(spec));

        Assert.Equal(
            ["control-plane", "control-plane", "control-plane", "worker", "worker"], lines);
    }

    [Fact]
    public void The_header_names_the_schema_kind_expects()
    {
        var yaml = KindConfig.Write(new LocalClusterSpec("dev") { WorkerNodes = 1 });

        Assert.StartsWith("kind: Cluster", yaml, StringComparison.Ordinal);
        Assert.Contains("apiVersion: kind.x-k8s.io/v1alpha4", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Port_mappings_land_on_the_first_control_plane_node_only()
    {
        var spec = new LocalClusterSpec("dev")
        {
            ControlPlaneNodes = 2,
            PortMappings = [new ClusterPortMapping(8080, 80)],
        };

        var yaml = KindConfig.Write(spec);

        Assert.Single(Occurrences(yaml, "extraPortMappings:"));

        // …and before the second control-plane node, i.e. inside the first one's block.
        var mappings = yaml.IndexOf("extraPortMappings:", StringComparison.Ordinal);
        var second = yaml.IndexOf("  - role: control-plane", mappings, StringComparison.Ordinal);
        Assert.True(mappings < second);
    }

    [Fact]
    public void A_port_mapping_carries_both_ports_and_an_upper_case_protocol()
    {
        var spec = new LocalClusterSpec("dev")
        {
            PortMappings = [new ClusterPortMapping(8080, 80), new ClusterPortMapping(5353, 53, "udp")],
        };

        var yaml = KindConfig.Write(spec);

        Assert.Contains("containerPort: 80", yaml, StringComparison.Ordinal);
        Assert.Contains("hostPort: 8080", yaml, StringComparison.Ordinal);
        Assert.Contains("protocol: TCP", yaml, StringComparison.Ordinal);
        Assert.Contains("protocol: UDP", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void A_listen_address_is_written_only_when_it_was_asked_for()
    {
        var bound = KindConfig.Write(new LocalClusterSpec("dev")
        {
            PortMappings = [new ClusterPortMapping(8080, 80, ListenAddress: "127.0.0.1")],
        });

        var open = KindConfig.Write(new LocalClusterSpec("dev")
        {
            PortMappings = [new ClusterPortMapping(8080, 80)],
        });

        Assert.Contains("listenAddress: \"127.0.0.1\"", bound, StringComparison.Ordinal);
        Assert.DoesNotContain("listenAddress", open, StringComparison.Ordinal);
    }

    [Fact]
    public void Ingress_labels_the_first_node_rather_than_installing_a_controller()
    {
        var yaml = KindConfig.Write(new LocalClusterSpec("dev") { IngressReady = true });

        Assert.Contains("node-labels: \"ingress-ready=true\"", yaml, StringComparison.Ordinal);
        Assert.Contains("kind: InitConfiguration", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ingress-nginx", yaml, StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> Roles(string yaml) =>
    [
        .. yaml.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("- role:", StringComparison.Ordinal))
            .Select(l => l["- role:".Length..].Trim()),
    ];

    private static List<int> Occurrences(string text, string needle)
    {
        var found = new List<int>();
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + 1, StringComparison.Ordinal))
        {
            found.Add(i);
        }

        return found;
    }
}
