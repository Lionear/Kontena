using Kontena.Core.Orchestration.Fakes;
using Kontena.Core.Orchestration.Provisioning;
using Kontena.Core.Tooling;
using Xunit;

namespace Kontena.Core.Orchestration.Tests;

public class FakeClusterProvisionerTests
{
    [Fact]
    public async Task Starts_with_nothing_and_reports_a_ready_tool()
    {
        var provisioner = new FakeClusterProvisioner();

        Assert.Empty(await provisioner.ListAsync());
        Assert.Equal(ToolState.Ready, (await provisioner.CheckAsync()).State);
    }

    [Fact]
    public async Task A_created_cluster_shows_up_in_the_list_with_a_context()
    {
        var provisioner = new FakeClusterProvisioner();

        await foreach (var _ in provisioner.CreateAsync(new LocalClusterSpec("dev")))
        {
        }

        var cluster = Assert.Single(await provisioner.ListAsync());
        Assert.Equal("dev", cluster.Name);
        Assert.Equal("fake-dev", cluster.Context);
        Assert.Single(provisioner.Created);
    }

    [Fact]
    public async Task A_scripted_failure_throws_after_the_lines_and_creates_nothing()
    {
        var provisioner = new FakeClusterProvisioner { CreateExitCode = 1 };

        await Assert.ThrowsAsync<ToolFailedException>(async () =>
        {
            await foreach (var _ in provisioner.CreateAsync(new LocalClusterSpec("dev")))
            {
            }
        });

        Assert.Empty(await provisioner.ListAsync());
    }

    [Fact]
    public async Task Deleting_removes_it_and_is_remembered()
    {
        var provisioner = new FakeClusterProvisioner().WithCluster("dev");

        await provisioner.DeleteAsync("dev");

        Assert.Empty(await provisioner.ListAsync());
        Assert.Equal(["dev"], provisioner.Deleted);
    }

    [Fact]
    public async Task An_unusable_name_is_refused_here_too()
    {
        var provisioner = new FakeClusterProvisioner();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in provisioner.CreateAsync(new LocalClusterSpec("Dev")))
            {
            }
        });
    }
}
