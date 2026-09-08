using Kontena.Sdk.Orchestration.Provisioning;
using Kontena.Sdk.Tooling;
using Kontena.Sdk.Tooling.Fakes;
using Xunit;

namespace Kontena.Adapters.RemoteClusters.Tests;

public class K0sClusterProvisionerTests
{
    /// <summary>
    /// A store rooted in a temp directory. Without it the default root is the user's real config
    /// directory, and a test would read whatever this machine happens to have downloaded.
    /// </summary>
    private static ManagedToolStore EmptyStore() =>
        new(Path.Combine(Path.GetTempPath(), $"kontena-tests-{Guid.NewGuid():N}"));

    private static K0sClusterProvisioner Provisioner(FakeToolRunner? runner = null) =>
        new(runner ?? new FakeToolRunner(), EmptyStore());

    private static readonly SshCredentials Login = new("rick") { KeyPath = "/home/rick/.ssh/id_ed25519" };

    private static RemoteClusterSpec Spec() =>
        new("prod-eu-west", [
            new RemoteClusterHost("10.10.4.11", ClusterHostRole.Controller),
            new RemoteClusterHost("10.10.4.21", ClusterHostRole.Worker),
        ]);

    [Fact]
    public async Task Without_k0sctl_the_check_says_missing_and_offers_an_install()
    {
        var readiness = await Provisioner().CheckAsync();

        Assert.Equal(ToolState.Missing, readiness.State);
        Assert.NotNull(readiness.Hint);
    }

    [Fact]
    public async Task A_k0sctl_that_is_there_is_reported_as_usable()
    {
        var runner = new FakeToolRunner().Install(KnownTools.K0sctl, "k0sctl version v0.19.2");

        Assert.True((await Provisioner(runner).CheckAsync()).Usable);
    }

    [Fact]
    public void It_needs_hosts_speaks_ssh_and_can_be_preflighted()
    {
        var capabilities = Provisioner().Capabilities;

        Assert.True(capabilities.NeedsHosts);
        Assert.Equal(ProvisionerTransport.Ssh, capabilities.Transport);
        Assert.True(capabilities.SupportsPreflight);
        Assert.True(capabilities.ChoosesCni);
        Assert.True(capabilities.HighAvailability);
    }

    [Fact]
    public void Nothing_that_only_makes_sense_for_a_local_tool_is_claimed()
    {
        var capabilities = Provisioner().Capabilities;

        // Ports, an ingress label and a runtime are all things a local tool does to containers it
        // owns. None of them mean anything on somebody else's machines.
        Assert.False(capabilities.PortMappings);
        Assert.False(capabilities.IngressReady);
        Assert.False(capabilities.StartStop);
        Assert.False(capabilities.Resources);
        Assert.Empty(capabilities.Runtimes);
    }

    [Fact]
    public async Task It_offers_no_version_list_rather_than_guessing_or_going_online()
    {
        // KON-144's rule is "ask the tool where the tool can be asked". k0sctl cannot be asked, and
        // the alternatives are a table that makes us the source of truth (rejected in KON-95) or a
        // network call from a local-first desktop app (rejected in KON-95 and KON-226).
        var versions = await Provisioner().VersionsAsync();

        Assert.Empty(versions.Offered);
        Assert.Null(versions.Default);
    }

    [Fact]
    public void The_preview_is_the_config_that_will_actually_run()
    {
        var preview = Provisioner().Preview(Spec(), Login);

        Assert.Equal(K0sctlConfig.Write(Spec(), Login), preview);
    }

    [Fact]
    public void A_talosconfig_is_refused_because_k0sctl_cannot_use_one()
    {
        var talos = new TalosCredentials { IsStored = true };

        var error = Assert.Throws<ArgumentException>(() => Provisioner().Preview(Spec(), talos));

        Assert.Contains("SSH", error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ProvisionerTransport.MachineApi), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_is_run_without_waiting_for_a_confirmation_nobody_can_give()
    {
        var arguments = K0sClusterProvisioner.Arguments("/tmp/prod.yaml");

        Assert.Equal("apply", arguments[0]);
        Assert.Contains("--config", arguments);
        Assert.Contains("/tmp/prod.yaml", arguments);

        // k0sctl prompts on a terminal, and there is none here; the wizard already confirmed.
        Assert.Contains("--force", arguments);

        // Not --no-wait: returning before the nodes are up would report success for something that is
        // still happening.
        Assert.DoesNotContain("--no-wait", arguments);
    }

    [Fact]
    public async Task A_missing_k0sctl_stops_a_create_before_anything_is_written()
    {
        var provisioner = Provisioner();

        await Assert.ThrowsAsync<ToolNotFoundException>(async () =>
        {
            await foreach (var _ in provisioner.CreateAsync(Spec(), Login))
            {
                // Enumerating is what starts it; nothing here should be reached.
            }
        });
    }

    /// <summary>
    /// KON-431: the config file was named after the cluster, so a rooted name or one holding
    /// <c>..</c> made Path.Combine drop the temp directory — and the cleanup in the finally deletes
    /// that path's parent recursively. The name no longer reaches the path at all.
    /// </summary>
    [Fact]
    public async Task The_config_file_is_named_for_the_run_not_for_the_cluster()
    {
        var runner = new FakeToolRunner().Install(KnownTools.K0sctl);

        await foreach (var _ in Provisioner(runner).CreateAsync(Spec(), Login))
        {
            // Draining the stream is what runs it.
        }

        var invocation = Assert.Single(runner.Invocations);
        var arguments = invocation.Arguments.ToList();
        var path = arguments[arguments.IndexOf("--config") + 1];

        Assert.DoesNotContain("prod-eu-west", path, StringComparison.Ordinal);
        Assert.Equal("k0sctl.yaml", Path.GetFileName(path));

        // And it lives in a directory of this run's own, under the temp directory — which is the one
        // the finally deleted, so nothing above it was ever a candidate.
        Assert.StartsWith(
            Path.Combine(Path.GetTempPath(), "kontena-k0sctl-"),
            Path.GetDirectoryName(path),
            StringComparison.Ordinal);

        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    /// <summary>
    /// The same name, refused outright. <see cref="K0sctlConfig.Write"/> happens to reject it first
    /// today, but the sink must not depend on the order of the lines above it.
    /// </summary>
    [Fact]
    public async Task A_name_that_would_walk_out_of_the_temp_directory_is_refused_and_nothing_runs()
    {
        var runner = new FakeToolRunner().Install(KnownTools.K0sctl);
        var provisioner = Provisioner(runner);
        var spec = new RemoteClusterSpec(
            "/etc/kontena/cluster",
            [new RemoteClusterHost("10.10.4.11", ClusterHostRole.Controller)]);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in provisioner.CreateAsync(spec, Login))
            {
                // Enumerating is what starts it; nothing here should be reached.
            }
        });

        Assert.Empty(runner.Invocations);
    }
}
