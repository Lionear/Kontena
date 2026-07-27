using Kontena.Core.Orchestration.Provisioning;
using Kontena.Core.Tooling;
using Kontena.Core.Tooling.Fakes;
using Xunit;

namespace Kontena.Adapters.LocalClusters.Tests;

public class KindClusterProvisionerTests
{
    /// <summary>
    /// A store rooted in a temp directory. Without it the default root is the user's real config
    /// directory, and a test would read whatever this machine happens to have downloaded.
    /// </summary>
    private static ManagedToolStore EmptyStore() =>
        new(Path.Combine(Path.GetTempPath(), $"kontena-tests-{Guid.NewGuid():N}"));

    private static KindClusterProvisioner Provisioner(FakeToolRunner runner) =>
        new(runner, EmptyStore());

    [Fact]
    public async Task Without_kind_the_check_says_missing_and_offers_an_install()
    {
        var readiness = await Provisioner(new FakeToolRunner()).CheckAsync();

        Assert.Equal(ToolState.Missing, readiness.State);
        Assert.NotNull(readiness.Hint);
    }

    [Fact]
    public async Task A_kind_that_is_too_old_is_usable_but_flagged()
    {
        var runner = new FakeToolRunner().Install(KnownTools.Kind, "kind v0.11.1 go1.16 linux/amd64");

        var readiness = await Provisioner(runner).CheckAsync();

        Assert.Equal(ToolState.Outdated, readiness.State);
        Assert.True(readiness.Usable);
    }

    [Fact]
    public async Task Listing_without_kind_is_empty_rather_than_an_error()
    {
        Assert.Empty(await Provisioner(new FakeToolRunner()).ListAsync());
    }

    [Fact]
    public async Task Clusters_are_read_from_kinds_output_with_their_kubeconfig_context()
    {
        var runner = new FakeToolRunner()
            .Install(KnownTools.Kind, "kind v0.31.0")
            .When(i => i.Arguments.Contains("clusters"), output: ["dev", "staging"]);

        var clusters = await Provisioner(runner).ListAsync();

        Assert.Equal(["dev", "staging"], clusters.Select(c => c.Name));
        Assert.Equal(["kind-dev", "kind-staging"], clusters.Select(c => c.Context));
        Assert.All(clusters, c => Assert.Equal("kind", c.Provisioner));
    }

    [Fact]
    public async Task The_no_clusters_sentence_is_not_read_as_a_cluster()
    {
        var runner = new FakeToolRunner()
            .Install(KnownTools.Kind, "kind v0.31.0")
            .When(i => i.Arguments.Contains("clusters"), output: ["No kind clusters found."]);

        Assert.Empty(await Provisioner(runner).ListAsync());
    }

    [Fact]
    public async Task Creating_streams_the_tools_own_words()
    {
        var runner = new FakeToolRunner()
            .Install(KnownTools.Kind, "kind v0.31.0")
            .When(i => i.Arguments.Contains("create"), errorOutput: ["Preparing nodes", "Ready"]);

        var lines = new List<string>();
        await foreach (var line in Provisioner(runner).CreateAsync(new LocalClusterSpec("dev")))
            lines.Add(line.Text);

        Assert.Equal(["Preparing nodes", "Ready"], lines);
        Assert.Contains("--name", runner.Invocations[^1].Arguments);
    }

    [Fact]
    public async Task A_failing_create_throws_at_the_end_of_the_stream()
    {
        var runner = new FakeToolRunner()
            .Install(KnownTools.Kind, "kind v0.31.0")
            .When(
                i => i.Arguments.Contains("create"),
                errorOutput: ["ERROR: failed to create cluster"],
                exitCode: 1);

        var lines = new List<string>();

        await Assert.ThrowsAsync<ToolFailedException>(async () =>
        {
            await foreach (var line in Provisioner(runner).CreateAsync(new LocalClusterSpec("dev")))
                lines.Add(line.Text);
        });

        // The lines still arrived: a console shows what went wrong, and the throw is what stops it
        // being reported as success.
        Assert.Single(lines);
    }

    [Fact]
    public async Task A_multi_node_create_writes_a_config_and_cleans_it_up()
    {
        var runner = new FakeToolRunner().Install(KnownTools.Kind, "kind v0.31.0");
        var spec = new LocalClusterSpec("dev") { WorkerNodes = 2 };

        await foreach (var _ in Provisioner(runner).CreateAsync(spec))
        {
        }

        var arguments = runner.Invocations[^1].Arguments;
        var configPath = arguments[arguments.ToList().IndexOf("--config") + 1];

        Assert.EndsWith(".yaml", configPath, StringComparison.Ordinal);
        Assert.False(File.Exists(configPath));
    }

    [Fact]
    public async Task Podman_is_asked_for_through_kinds_own_variable()
    {
        var runner = new FakeToolRunner().Install(KnownTools.Kind, "kind v0.31.0");
        var spec = new LocalClusterSpec("dev") { Runtime = LocalClusterRuntime.Podman };

        await foreach (var _ in Provisioner(runner).CreateAsync(spec))
        {
        }

        Assert.Equal(
            "podman", runner.Invocations[^1].Environment["KIND_EXPERIMENTAL_PROVIDER"]);
    }

    [Fact]
    public async Task Choosing_Docker_clears_the_variable_rather_than_trusting_the_shell()
    {
        var runner = new FakeToolRunner().Install(KnownTools.Kind, "kind v0.31.0");
        var spec = new LocalClusterSpec("dev") { Runtime = LocalClusterRuntime.Docker };

        await foreach (var _ in Provisioner(runner).CreateAsync(spec))
        {
        }

        var environment = runner.Invocations[^1].Environment;
        Assert.True(environment.ContainsKey("KIND_EXPERIMENTAL_PROVIDER"));
        Assert.Null(environment["KIND_EXPERIMENTAL_PROVIDER"]);
    }

    [Fact]
    public async Task The_default_runtime_leaves_the_environment_alone()
    {
        var runner = new FakeToolRunner().Install(KnownTools.Kind, "kind v0.31.0");

        await foreach (var _ in Provisioner(runner).CreateAsync(new LocalClusterSpec("dev")))
        {
        }

        Assert.Empty(runner.Invocations[^1].Environment);
    }

    [Fact]
    public async Task A_name_that_cannot_be_used_is_refused_before_anything_runs()
    {
        var runner = new FakeToolRunner().Install(KnownTools.Kind, "kind v0.31.0");

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in Provisioner(runner).CreateAsync(new LocalClusterSpec("Dev Cluster")))
            {
            }
        });

        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task Deleting_names_the_cluster_and_reports_a_refusal()
    {
        var runner = new FakeToolRunner()
            .Install(KnownTools.Kind, "kind v0.31.0")
            .When(i => i.Arguments.Contains("delete"), errorOutput: ["ERROR: unknown cluster"], exitCode: 1);

        var error = await Assert.ThrowsAsync<ToolFailedException>(
            async () => await Provisioner(runner).DeleteAsync("dev"));

        Assert.Contains("unknown cluster", error.Complaint, StringComparison.Ordinal);
        Assert.Equal(["delete", "cluster", "--name", "dev"], runner.Invocations[^1].Arguments);
    }

    [Fact]
    public void Kind_cannot_pause_a_cluster_and_says_so()
    {
        var capabilities = Provisioner(new FakeToolRunner()).Capabilities;

        Assert.False(capabilities.StartStop);
        Assert.False(capabilities.Resources);
        Assert.True(capabilities.MultiNode);
        Assert.True(capabilities.PortMappings);
    }
}
