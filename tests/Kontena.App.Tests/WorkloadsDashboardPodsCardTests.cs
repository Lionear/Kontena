using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// The Pods card on the Workloads dashboard (KON-341). Pods are not a workload kind, but they are an
/// entry in this section of the sidebar, and a page whose subtitle says "pick a kind" left one of its
/// own section's entries off the page.
/// </summary>
public sealed class WorkloadsDashboardPodsCardTests
{
    private static async Task<ClusterWorkloadsDashboardViewModel> DashboardAsync(
        FakeClusterEngine cluster, string? ns = null)
    {
        var page = new ClusterWorkloadsDashboardViewModel(cluster, ns);
        await page.LoadAsync();
        return page;
    }

    [Fact]
    public async Task Pods_gets_a_card_directly_after_Deployments()
    {
        var cluster = new FakeClusterEngine();
        var page = await DashboardAsync(cluster);

        var labels = page.Kinds.Select(k => k.Label).ToList();
        Assert.Equal("Pods", labels[labels.IndexOf("Deployments") + 1]);

        // Same rule as the sidebar (KON-342), so the two views do not disagree about where Pods sits.
        Assert.Contains("DaemonSets", labels);
    }

    [Fact]
    public async Task The_card_counts_the_pods_and_splits_them_by_phase()
    {
        var cluster = new FakeClusterEngine();
        var page = await DashboardAsync(cluster);
        var pods = await cluster.ListPodsAsync();

        var card = page.Kinds.Single(k => k.Label == "Pods");
        Assert.Equal(pods.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), card.Count);
        Assert.Equal("pods", card.Unit);

        // The seed is deliberately not all-healthy, so both halves of the split have something to say.
        var running = pods.Count(p => p.Phase == PodPhase.Running);
        var pending = pods.Count(p => p.Phase == PodPhase.Pending);
        Assert.True(running > 0 && pending > 0, "the fake should seed a mixed set of phases");

        Assert.Contains(card.Legend, l => l.Text == $"{running} running");
        Assert.Contains(card.Legend, l => l.Text == $"{pending} pending");

        // Never a legend entry for a phase nothing is in — the rule the kind cards already follow.
        Assert.DoesNotContain(card.Legend, l => l.Text.EndsWith(" completed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_Deployments_the_card_goes_last()
    {
        // "monitoring" runs one DaemonSet and nothing else: no Deployments card to sit under, which is
        // the sidebar's fallback as well.
        var cluster = new FakeClusterEngine();
        var page = await DashboardAsync(cluster, "monitoring");

        var labels = page.Kinds.Select(k => k.Label).ToList();
        Assert.DoesNotContain("Deployments", labels);
        Assert.Equal("Pods", labels[^1]);
    }
}
