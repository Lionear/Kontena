using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// Where Pods sits in the Workloads section (Rick, 2026-08-03). Directly under Deployments: the pods
/// you go looking for are nearly always a Deployment's, and Pods used to sit at the foot of the kinds
/// with every other kind between the two.
/// <para>
/// Asserted on the order of the group rather than on an index, because the kinds above it come and go
/// with the namespace — an index is exactly the thing this is not allowed to depend on.
/// </para>
/// </summary>
public sealed class WorkloadNavOrderTests
{
    private static async Task<IReadOnlyList<string>> WorkloadSectionAsync(string? ns = null)
    {
        var shell = new MainWindowViewModel();
        Assert.True(await shell.EnterClusterModeAsync(new FakeClusterEngine()));

        if (ns is not null)
            shell.SelectedNamespace = ns;

        return [.. shell.NavGroups.Single(g => g.Label == "Workloads").Items.Select(i => i.Key)];
    }

    [Fact]
    public async Task Pods_comes_directly_after_Deployments()
    {
        var keys = await WorkloadSectionAsync();
        var deployments = WorkloadNavGroups.KeyFor(WorkloadKind.Deployment);

        // Across all namespaces the fake runs five kinds, so there is plenty for Pods to be buried under.
        Assert.Contains(WorkloadNavGroups.KeyFor(WorkloadKind.DaemonSet), keys);
        Assert.Equal("pods", keys[keys.ToList().IndexOf(deployments) + 1]);
    }

    [Fact]
    public async Task Without_Deployments_Pods_stays_where_it_was()
    {
        // "monitoring" runs one DaemonSet and nothing else: no Deployments entry to sit under, and one
        // kind draws no sub-entries at all (KON-219). Pods keeps its place at the end.
        var keys = await WorkloadSectionAsync("monitoring");

        Assert.DoesNotContain(WorkloadNavGroups.KeyFor(WorkloadKind.Deployment), keys);
        Assert.Equal("pods", keys[^1]);
    }
}
