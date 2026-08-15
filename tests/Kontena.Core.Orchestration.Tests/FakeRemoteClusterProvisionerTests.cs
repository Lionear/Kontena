using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Provisioning;
using Kontena.Sdk.Tooling;
using Xunit;

namespace Kontena.Core.Orchestration.Tests;

public class FakeRemoteClusterProvisionerTests
{
    private static RemoteClusterSpec Spec() =>
        new("prod-eu-west", [
            new RemoteClusterHost("10.10.4.11", ClusterHostRole.Controller),
            new RemoteClusterHost("10.10.4.12", ClusterHostRole.Controller),
            new RemoteClusterHost("10.10.4.13", ClusterHostRole.Controller),
            new RemoteClusterHost("10.10.4.21", ClusterHostRole.Worker),
            new RemoteClusterHost("10.10.4.22", ClusterHostRole.Worker),
        ]);

    private static readonly SshCredentials Login = new("rick");

    private static async Task<List<string>> LinesAsync(FakeRemoteClusterProvisioner provisioner)
    {
        var lines = new List<string>();

        await foreach (var line in provisioner.CreateAsync(Spec(), Login))
            lines.Add(line.Text);

        return lines;
    }

    [Fact]
    public async Task It_streams_a_rollout_that_touches_nothing()
    {
        var provisioner = new FakeRemoteClusterProvisioner();

        var lines = await LinesAsync(provisioner);

        Assert.Contains(lines, l => l.Contains("10.10.4.11", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("prod-eu-west is ready", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Controllers_are_installed_before_workers_so_a_half_finished_screenshot_is_believable()
    {
        var lines = await LinesAsync(new FakeRemoteClusterProvisioner());

        var lastController = lines.FindLastIndex(l => l.Contains("k0s controller", StringComparison.Ordinal));
        var firstWorker = lines.FindIndex(l => l.Contains("k0s worker", StringComparison.Ordinal));

        Assert.True(lastController < firstWorker);
    }

    [Fact]
    public async Task It_records_what_it_was_asked_to_install()
    {
        var provisioner = new FakeRemoteClusterProvisioner();

        await LinesAsync(provisioner);

        Assert.Equal("prod-eu-west", Assert.Single(provisioner.Created).Name);
        Assert.Same(Login, Assert.Single(provisioner.Credentials));
    }

    [Fact]
    public async Task Asked_to_fail_it_throws_at_the_end_of_enumeration_as_the_real_one_does()
    {
        var provisioner = new FakeRemoteClusterProvisioner { FailAfter = 3 };
        var lines = new List<string>();

        var error = await Assert.ThrowsAsync<ToolFailedException>(async () =>
        {
            await foreach (var line in provisioner.CreateAsync(Spec(), Login))
                lines.Add(line.Text);
        });

        // The lines before the failure still arrived — a caller rendering them shows a partial
        // rollout, which is what a failed one looks like.
        Assert.Equal(3, lines.Count);
        Assert.Equal(1, error.ExitCode);
    }

    [Fact]
    public void It_declares_itself_a_remote_provisioner_so_the_wizard_shows_the_right_form()
    {
        var capabilities = new FakeRemoteClusterProvisioner().Capabilities;

        Assert.True(capabilities.NeedsHosts);
        Assert.Equal(ProvisionerTransport.Ssh, capabilities.Transport);
    }

    [Fact]
    public void The_preview_is_not_empty_so_a_demo_has_something_to_show()
    {
        var preview = new FakeRemoteClusterProvisioner().Preview(Spec(), Login);

        Assert.Contains("prod-eu-west", preview, StringComparison.Ordinal);
        Assert.Contains("10.10.4.11", preview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task It_answers_no_version_list_by_default_exactly_as_the_real_one_does()
    {
        Assert.Empty((await new FakeRemoteClusterProvisioner().VersionsAsync()).Offered);
    }
}
