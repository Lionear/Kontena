using Kontena.Core.Orchestration;
using Kontena.Sdk.Orchestration.Provisioning;
using Xunit;

namespace Kontena.Core.Orchestration.Tests;

public class K0sctlImportTests
{
    private const string Real = """
        apiVersion: k0sctl.k0sproject.io/v1beta1
        kind: Cluster
        metadata:
          name: prod-eu-west
        spec:
          hosts:
            - ssh:
                address: 10.10.4.11
                user: rick
                keyPath: ~/.ssh/id_ed25519
              role: controller
            - ssh:
                address: 10.10.4.12
                user: rick
                keyPath: ~/.ssh/id_ed25519
              role: controller
            - ssh:
                address: 10.10.4.21
                user: rick
              role: worker
          k0s:
            version: v1.32.1+k0s.0
        """;

    [Fact]
    public void A_k0sctl_file_gives_its_machines_in_order()
    {
        var hosts = K0sctlImport.ReadHosts(Real);

        Assert.Equal(3, hosts.Count);
        Assert.Equal(["10.10.4.11", "10.10.4.12", "10.10.4.21"], hosts.Select(h => h.Address));
        Assert.Equal(
            [ClusterHostRole.Controller, ClusterHostRole.Controller, ClusterHostRole.Worker],
            hosts.Select(h => h.Role));
    }

    [Fact]
    public void The_login_details_come_across_but_never_a_key()
    {
        var host = K0sctlImport.ReadHosts(Real)[0];

        Assert.Equal("rick", host.User);
        Assert.Equal("~/.ssh/id_ed25519", host.KeyPath);
    }

    [Fact]
    public void A_host_without_a_key_path_leans_on_the_agent()
    {
        Assert.Null(K0sctlImport.ReadHosts(Real)[2].KeyPath);
    }

    [Fact]
    public void An_ssh_block_written_inline_reads_the_same()
    {
        const string yaml = """
            spec:
              hosts:
                - ssh: {address: 10.10.4.11, user: rick, keyPath: ~/.ssh/id_ed25519}
                  role: controller
            """;

        var host = Assert.Single(K0sctlImport.ReadHosts(yaml));

        Assert.Equal("10.10.4.11", host.Address);
        Assert.Equal("rick", host.User);
        Assert.Equal("~/.ssh/id_ed25519", host.KeyPath);
        Assert.Equal(ClusterHostRole.Controller, host.Role);
    }

    [Fact]
    public void An_inline_block_that_runs_over_two_lines_is_pulled_back_together()
    {
        const string yaml = """
            spec:
              hosts:
                - ssh: {address: 10.10.4.11, user: rick,
                        keyPath: ~/.ssh/id_ed25519}
                  role: controller
            """;

        var host = Assert.Single(K0sctlImport.ReadHosts(yaml));

        Assert.Equal("10.10.4.11", host.Address);
        Assert.Equal("~/.ssh/id_ed25519", host.KeyPath);
    }

    [Theory]
    [InlineData("controller", ClusterHostRole.Controller)]
    [InlineData("controller+worker", ClusterHostRole.Controller)]
    [InlineData("single", ClusterHostRole.Controller)]
    [InlineData("worker", ClusterHostRole.Worker)]
    public void The_k0sctl_role_words_map_onto_the_two_roles_a_spec_has(string role, ClusterHostRole expected)
    {
        var yaml = $$"""
            spec:
              hosts:
                - ssh: {address: 10.10.4.11}
                  role: {{role}}
            """;

        Assert.Equal(expected, Assert.Single(K0sctlImport.ReadHosts(yaml)).Role);
    }

    [Fact]
    public void A_host_with_no_role_is_a_worker_which_is_what_k0sctl_assumes()
    {
        const string yaml = """
            spec:
              hosts:
                - ssh: {address: 10.10.4.11}
            """;

        Assert.Equal(ClusterHostRole.Worker, Assert.Single(K0sctlImport.ReadHosts(yaml)).Role);
    }

    [Fact]
    public void Comments_and_blank_lines_are_ignored()
    {
        const string yaml = """
            # the production cluster
            spec:
              hosts:

                # the one that runs etcd
                - ssh:
                    address: 10.10.4.11   # first controller
                  role: controller
            """;

        var host = Assert.Single(K0sctlImport.ReadHosts(yaml));

        Assert.Equal("10.10.4.11", host.Address);
        Assert.Equal(ClusterHostRole.Controller, host.Role);
    }

    [Fact]
    public void Quoted_values_lose_their_quotes()
    {
        const string yaml = """
            spec:
              hosts:
                - ssh:
                    address: "10.10.4.11"
                    user: 'rick'
                  role: "controller"
            """;

        var host = Assert.Single(K0sctlImport.ReadHosts(yaml));

        Assert.Equal("10.10.4.11", host.Address);
        Assert.Equal("rick", host.User);
        Assert.Equal(ClusterHostRole.Controller, host.Role);
    }

    [Fact]
    public void The_list_stops_where_the_hosts_block_does()
    {
        // k0s: sits at the same level as hosts:, so its contents are not machines.
        Assert.Equal(3, K0sctlImport.ReadHosts(Real).Count);
    }

    [Fact]
    public void A_host_we_cannot_reach_over_ssh_is_left_out_rather_than_guessed_at()
    {
        const string yaml = """
            spec:
              hosts:
                - localhost:
                    enabled: true
                  role: controller
                - ssh: {address: 10.10.4.21}
                  role: worker
            """;

        Assert.Equal("10.10.4.21", Assert.Single(K0sctlImport.ReadHosts(yaml)).Address);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("just: some yaml")]
    [InlineData("spec:\n  hosts: []")]
    public void Anything_without_a_readable_host_list_gives_nothing(string yaml)
    {
        Assert.Empty(K0sctlImport.ReadHosts(yaml));
    }

    [Fact]
    public void A_null_file_is_nothing_rather_than_an_exception()
    {
        Assert.Empty(K0sctlImport.ReadHosts(null));
    }

    [Fact]
    public void What_comes_out_is_something_the_spec_will_accept()
    {
        var spec = new RemoteClusterSpec("prod-eu-west", K0sctlImport.ReadHosts(Real));

        Assert.Null(spec.Problem());
        Assert.Equal(2, spec.ControllerCount);

        // Two controllers, so it warns — the import does not launder a shape we would flag if typed.
        Assert.Contains(spec.Warnings(), w => w.Contains("quorum", StringComparison.Ordinal));
    }
}
