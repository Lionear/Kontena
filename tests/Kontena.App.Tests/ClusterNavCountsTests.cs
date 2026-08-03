using System.Globalization;
using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// Which badge carries which count (KON-338). The twelve listers behind the sidebar are now started
/// together and read afterwards, so the number and the entry it sits on are no longer bound by the
/// line they were written on — and a badge showing another resource's total is the way that goes
/// wrong. Compared against the engine rather than against literals, so reseeding the fake does not
/// silently rewrite what the test claims.
/// </summary>
public sealed class ClusterNavCountsTests
{
    [Fact]
    public async Task Every_badge_carries_the_count_of_its_own_resource()
    {
        var engine = new FakeClusterEngine();
        var shell = new MainWindowViewModel();
        Assert.True(await shell.EnterClusterModeAsync(engine));

        var ci = CultureInfo.InvariantCulture;
        var warnings = (await engine.ListEventsAsync()).Count(e => e.Severity == EventSeverity.Warning);
        var expected = new Dictionary<string, string>
        {
            ["nodes"] = (await engine.ListNodesAsync()).Count.ToString(ci),
            ["namespaces"] = (await engine.ListNamespacesAsync()).Count.ToString(ci),
            ["workloads"] = (await engine.ListWorkloadsAsync()).Count.ToString(ci),
            ["pods"] = (await engine.ListPodsAsync()).Count.ToString(ci),
            ["services"] = (await engine.ListServicesAsync()).Count.ToString(ci),
            ["configmaps"] = (await engine.ListConfigMapsAsync()).Count.ToString(ci),
            ["secrets"] = (await engine.ListSecretsAsync()).Count.ToString(ci),
            ["ingresses"] = (await engine.ListIngressesAsync()).Count.ToString(ci),
            ["pvcs"] = (await engine.ListPvcsAsync()).Count.ToString(ci),
            ["volumes"] = (await engine.ListVolumesAsync()).Count.ToString(ci),
            ["storageclasses"] = (await engine.ListStorageClassesAsync()).Count.ToString(ci),

            // Warnings, not events (KON-248) — the one badge whose number is not a list length.
            ["events"] = warnings > 0 ? warnings.ToString(ci) : string.Empty,
        };

        var badges = shell.NavGroups.SelectMany(g => g.Items).ToDictionary(i => i.Key, i => i.Count);
        foreach (var (key, count) in expected)
            Assert.Equal(count, badges[key]);
    }

    [Fact]
    public async Task Switching_namespace_narrows_the_badges_to_it()
    {
        // The namespaced half of the same mapping: cluster-wide entries keep their total, the rest
        // follow the picker. A count read from the wrong task would show all namespaces here.
        var engine = new FakeClusterEngine();
        var shell = new MainWindowViewModel();
        Assert.True(await shell.EnterClusterModeAsync(engine));

        shell.SelectedNamespace = "monitoring";

        var ci = CultureInfo.InvariantCulture;
        var badges = shell.NavGroups.SelectMany(g => g.Items).ToDictionary(i => i.Key, i => i.Count);

        Assert.Equal((await engine.ListPodsAsync("monitoring")).Count.ToString(ci), badges["pods"]);
        Assert.Equal((await engine.ListWorkloadsAsync(null, "monitoring")).Count.ToString(ci), badges["workloads"]);

        // Nodes are not namespaced, so this one must not have moved with the picker.
        Assert.Equal((await engine.ListNodesAsync()).Count.ToString(ci), badges["nodes"]);
    }
}
