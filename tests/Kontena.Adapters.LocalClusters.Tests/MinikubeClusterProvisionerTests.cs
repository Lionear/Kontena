using Kontena.Core.Orchestration.Provisioning;
using Kontena.Core.Tooling;
using Kontena.Core.Tooling.Fakes;
using Xunit;

namespace Kontena.Adapters.LocalClusters.Tests;

public class MinikubeClusterProvisionerTests
{
    private static ManagedToolStore EmptyStore() =>
        new(Path.Combine(Path.GetTempPath(), $"kontena-tests-{Guid.NewGuid():N}"));

    private static MinikubeClusterProvisioner Provisioner(FakeToolRunner runner) => new(runner, EmptyStore());

    private const string TwoProfiles = """
        {"valid":[
          {"Name":"dev","Status":"Running","Config":{"Name":"dev","Driver":"docker","Nodes":[{"Name":""}]}},
          {"Name":"old","Status":"Stopped","Config":{"Name":"old","Driver":"kvm2","Nodes":[{"Name":""}]}}
        ]}
        """;

    [Fact]
    public async Task Without_minikube_the_check_says_missing_and_the_list_is_empty()
    {
        var provisioner = Provisioner(new FakeToolRunner());

        Assert.Equal(ToolState.Missing, (await provisioner.CheckAsync()).State);
        Assert.Empty(await provisioner.ListAsync());
    }

    [Fact]
    public async Task Profiles_are_read_as_clusters()
    {
        var runner = new FakeToolRunner()
            .Install(KnownTools.Minikube, "v1.34.0")
            .When(i => i.Arguments.Contains("profile"), output: [TwoProfiles]);

        var clusters = await Provisioner(runner).ListAsync();

        Assert.Equal(["dev", "old"], clusters.Select(c => c.Name));
        Assert.Equal(LocalClusterState.Running, clusters[0].State);
        Assert.Equal(LocalClusterState.Stopped, clusters[1].State);
        Assert.All(clusters, c => Assert.Equal("minikube", c.Provisioner));
    }

    [Fact]
    public async Task Creating_streams_the_tools_own_words()
    {
        var runner = new FakeToolRunner()
            .Install(KnownTools.Minikube, "v1.34.0")
            .When(i => i.Arguments.Contains("start"), output: ["😄  minikube v1.34.0", "🏄  Done!"]);

        var lines = new List<string>();
        var spec = new LocalClusterSpec("dev") { Cpus = 4, MemoryMb = 8192 };

        await foreach (var line in Provisioner(runner).CreateAsync(spec))
            lines.Add(line.Text);

        Assert.Equal(2, lines.Count);

        var arguments = runner.Invocations[^1].Arguments;
        Assert.Contains("--cpus", arguments);
        Assert.Contains("8192mb", arguments);
    }

    [Fact]
    public async Task Starting_a_stopped_cluster_is_the_same_command_without_a_spec()
    {
        var runner = new FakeToolRunner().Install(KnownTools.Minikube, "v1.34.0");

        await foreach (var _ in Provisioner(runner).StartAsync("dev"))
        {
        }

        Assert.Equal(["start", "--profile", "dev"], runner.Invocations[^1].Arguments);
    }

    [Fact]
    public async Task Stopping_reports_a_refusal_in_the_tools_own_words()
    {
        var runner = new FakeToolRunner()
            .Install(KnownTools.Minikube, "v1.34.0")
            .When(i => i.Arguments.Contains("stop"), errorOutput: ["profile \"dev\" not found"], exitCode: 1);

        var error = await Assert.ThrowsAsync<ToolFailedException>(
            async () => await Provisioner(runner).StopAsync("dev"));

        Assert.Contains("not found", error.Complaint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleting_names_the_profile()
    {
        var runner = new FakeToolRunner().Install(KnownTools.Minikube, "v1.34.0");

        await Provisioner(runner).DeleteAsync("dev");

        Assert.Equal(["delete", "--profile", "dev"], runner.Invocations[^1].Arguments);
    }

    [Fact]
    public async Task A_name_that_cannot_be_used_is_refused_before_anything_runs()
    {
        var runner = new FakeToolRunner().Install(KnownTools.Minikube, "v1.34.0");

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await Provisioner(runner).DeleteAsync("Dev Cluster"));

        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public void What_minikube_adds_over_kind_is_in_its_capabilities()
    {
        var capabilities = Provisioner(new FakeToolRunner()).Capabilities;

        Assert.True(capabilities.StartStop);
        Assert.True(capabilities.Resources);
        Assert.Contains(LocalClusterRuntime.Kvm2, capabilities.Runtimes);

        // And what it does not do the kind way: ingress is an addon here, not a create-time label.
        Assert.False(capabilities.IngressReady);
    }

    [Fact]
    public void The_context_is_the_profile_name_itself()
    {
        Assert.Equal("dev", MinikubeClusterProvisioner.ContextFor("dev"));
    }
}
