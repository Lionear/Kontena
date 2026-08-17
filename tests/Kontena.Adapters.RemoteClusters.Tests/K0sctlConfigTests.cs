using Kontena.Adapters.RemoteClusters;
using Kontena.Sdk.Orchestration.Provisioning;
using Xunit;

namespace Kontena.Adapters.RemoteClusters.Tests;

public class K0sctlConfigTests
{
    private static readonly SshCredentials Login = new("rick") { KeyPath = "/home/rick/.ssh/id_ed25519" };

    private static RemoteClusterSpec Spec(params (string Address, ClusterHostRole Role)[] hosts) =>
        new("prod-eu-west", [.. hosts.Select(h => new RemoteClusterHost(h.Address, h.Role))]);

    private const ClusterHostRole Controller = ClusterHostRole.Controller;
    private const ClusterHostRole Worker = ClusterHostRole.Worker;

    [Fact]
    public void One_document_describes_every_machine_and_its_role()
    {
        var yaml = K0sctlConfig.Write(
            Spec(("10.10.4.11", Controller), ("10.10.4.21", Worker)), Login);

        Assert.Contains("apiVersion: k0sctl.k0sproject.io/v1beta1", yaml, StringComparison.Ordinal);
        Assert.Contains("kind: Cluster", yaml, StringComparison.Ordinal);
        Assert.Contains("name: prod-eu-west", yaml, StringComparison.Ordinal);
        Assert.Contains("address: 10.10.4.11", yaml, StringComparison.Ordinal);
        Assert.Contains("role: controller", yaml, StringComparison.Ordinal);
        Assert.Contains("address: 10.10.4.21", yaml, StringComparison.Ordinal);
        Assert.Contains("role: worker", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cluster_login_is_written_for_every_host()
    {
        var yaml = K0sctlConfig.Write(Spec(("10.10.4.11", Controller), ("10.10.4.21", Worker)), Login);

        Assert.Equal(2, Occurrences(yaml, "user: rick"));
        Assert.Equal(2, Occurrences(yaml, "keyPath: /home/rick/.ssh/id_ed25519"));
    }

    [Fact]
    public void A_host_with_its_own_login_gets_it_and_the_others_do_not()
    {
        var spec = new RemoteClusterSpec("prod-eu-west", [
            new RemoteClusterHost("10.10.4.11", Controller),
            new RemoteClusterHost("10.10.4.21", Worker) { User = "ubuntu", KeyPath = "/keys/odd-one" },
        ]);

        var yaml = K0sctlConfig.Write(spec, Login);

        Assert.Contains("user: ubuntu", yaml, StringComparison.Ordinal);
        Assert.Contains("keyPath: /keys/odd-one", yaml, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(yaml, "user: rick"));
    }

    [Fact]
    public void No_key_means_the_agent_answers_so_no_keyPath_is_written()
    {
        var yaml = K0sctlConfig.Write(Spec(("10.10.4.11", Controller)), new SshCredentials("rick"));

        // An empty keyPath would be a path k0sctl then fails to find, which is worse than none.
        Assert.DoesNotContain("keyPath", yaml, StringComparison.Ordinal);
        Assert.Contains("user: rick", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void A_node_name_is_written_as_the_hostname_override()
    {
        var spec = new RemoteClusterSpec("prod-eu-west", [
            new RemoteClusterHost("10.10.4.11", Controller) { NodeName = "cp-1" },
        ]);

        Assert.Contains("hostname: cp-1", K0sctlConfig.Write(spec, Login), StringComparison.Ordinal);
    }

    // ── The k0s block ────────────────────────────────────────────────────────

    [Fact]
    public void A_spec_that_chose_nothing_writes_no_k0s_block_at_all()
    {
        var yaml = K0sctlConfig.Write(Spec(("10.10.4.11", Controller)), Login);

        // Omitting it is how k0sctl is told to install the latest stable it knows of. Writing a
        // version we guessed at would pin the cluster to our guess.
        Assert.DoesNotContain("k0s:", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void A_chosen_version_is_written_and_nothing_else_is()
    {
        var spec = Spec(("10.10.4.11", Controller)) with { KubernetesVersion = "v1.32.1+k0s.0" };

        var yaml = K0sctlConfig.Write(spec, Login);

        Assert.Contains("version: v1.32.1+k0s.0", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("config:", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void The_network_lands_in_the_embedded_cluster_config()
    {
        var spec = Spec(("10.10.4.11", Controller)) with
        {
            PodCidr = "10.244.0.0/16",
            ServiceCidr = "10.96.0.0/12",
            Cni = "calico",
        };

        var yaml = K0sctlConfig.Write(spec, Login);

        Assert.Contains("kind: ClusterConfig", yaml, StringComparison.Ordinal);
        Assert.Contains("podCIDR: 10.244.0.0/16", yaml, StringComparison.Ordinal);
        Assert.Contains("serviceCIDR: 10.96.0.0/12", yaml, StringComparison.Ordinal);
        Assert.Contains("provider: calico", yaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("kube-router", "kuberouter")]
    [InlineData("kuberouter", "kuberouter")]
    [InlineData("Calico", "calico")]
    [InlineData("cilium", "cilium")]
    public void The_cni_is_written_in_k0s_own_spelling_and_an_unknown_one_passes_through(string given, string expected)
    {
        // Passed through rather than translated, so an unknown name fails in k0s's words, not ours.
        Assert.Equal(expected, K0sctlConfig.Provider(given));
    }

    [Fact]
    public void The_control_plane_endpoint_becomes_the_external_address()
    {
        var spec = Spec(("10.10.4.11", Controller)) with { ControlPlaneEndpoint = "api.lan:6443" };

        var yaml = K0sctlConfig.Write(spec, Login);

        Assert.Contains("externalAddress: api.lan", yaml, StringComparison.Ordinal);
        Assert.Contains("port: 6443", yaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("api.lan:6443", "api.lan", 6443)]
    [InlineData("api.lan", "api.lan", null)]
    [InlineData("10.10.4.10", "10.10.4.10", null)]
    [InlineData("[fd00::1]:6443", "fd00::1", 6443)]
    [InlineData("fd00::1", "fd00::1", null)]
    [InlineData("api.lan:not-a-port", "api.lan:not-a-port", null)]
    public void An_endpoint_is_split_into_address_and_port_without_mangling_ipv6(
        string endpoint, string address, int? port)
    {
        // A colon left in the address becomes a certificate for a name with a colon in it.
        Assert.Equal((address, port), K0sctlConfig.SplitEndpoint(endpoint));
    }

    // ── Safety and shape ─────────────────────────────────────────────────────

    [Fact]
    public void A_spec_that_cannot_be_rolled_out_is_refused_before_any_machine_is_touched()
    {
        var workersOnly = Spec(("10.10.4.21", Worker));

        var error = Assert.Throws<ArgumentException>(() => K0sctlConfig.Write(workersOnly, Login));

        Assert.Contains("controller", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("~/.ssh/id_ed25519", "~/.ssh/id_ed25519")]
    [InlineData("v1.32.1+k0s.0", "v1.32.1+k0s.0")]
    [InlineData("has space", "'has space'")]
    [InlineData("has: colon", "'has: colon'")]
    [InlineData("#comment", "'#comment'")]
    [InlineData("it's", "'it''s'")]
    [InlineData("", "''")]
    public void A_value_that_would_change_the_documents_shape_gets_quoted(string value, string expected)
    {
        Assert.Equal(expected, K0sctlConfig.Scalar(value));
    }

    [Fact]
    public void What_it_writes_is_what_the_importer_reads_back()
    {
        // The reader from KON-233 and this writer are the same file from two ends; a round trip that
        // loses a host is the failure that would only show up in someone's real config.
        var spec = new RemoteClusterSpec("prod-eu-west", [
            new RemoteClusterHost("10.10.4.11", Controller),
            new RemoteClusterHost("10.10.4.12", Controller),
            new RemoteClusterHost("10.10.4.21", Worker) { User = "ubuntu" },
        ]);

        var hosts = Kontena.Core.Orchestration.K0sctlImport.ReadHosts(K0sctlConfig.Write(spec, Login));

        Assert.Equal(3, hosts.Count);
        Assert.Equal(["10.10.4.11", "10.10.4.12", "10.10.4.21"], hosts.Select(h => h.Address));
        Assert.Equal([Controller, Controller, Worker], hosts.Select(h => h.Role));
        Assert.Equal("ubuntu", hosts[2].User);
        Assert.Equal("/home/rick/.ssh/id_ed25519", hosts[0].KeyPath);
    }

    private static int Occurrences(string text, string needle)
    {
        var count = 0;

        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
