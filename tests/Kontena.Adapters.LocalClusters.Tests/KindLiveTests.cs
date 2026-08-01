using Kontena.Sdk.Tooling;
using Xunit;

namespace Kontena.Adapters.LocalClusters.Tests;

/// <summary>
/// The read-only half of the provisioner against a real kind, when this machine has one. Skips
/// cleanly when it does not, like the Docker and Kubernetes suites.
/// <para>
/// Read-only on purpose: creating a cluster takes minutes and leaves containers behind, which is not
/// something a test run should decide to do. What is worth checking here is the part a fake cannot —
/// that the command lines are the ones this kind accepts, and that its version string parses.
/// </para>
/// </summary>
public class KindLiveTests
{
    private static readonly KindClusterProvisioner Provisioner = new(new ToolRunner());

    [SkippableFact]
    public async Task Real_kind_reports_itself_as_ready()
    {
        var readiness = await Provisioner.CheckAsync();
        Skip.If(readiness.State == ToolState.Missing, "kind is not installed on this machine.");

        Assert.Equal(ToolState.Ready, readiness.State);
        Assert.NotNull(readiness.Version);
        Assert.NotNull(readiness.Path);
    }

    [SkippableFact]
    public async Task Listing_real_clusters_yields_kind_contexts()
    {
        Skip.If(
            (await Provisioner.CheckAsync()).State == ToolState.Missing,
            "kind is not installed on this machine.");

        var clusters = await Provisioner.ListAsync();

        Assert.All(clusters, c => Assert.Equal($"kind-{c.Name}", c.Context));
        Assert.All(clusters, c => Assert.NotEmpty(c.Name));
    }
}
