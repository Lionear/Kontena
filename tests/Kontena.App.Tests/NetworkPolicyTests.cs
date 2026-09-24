using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// The NetworkPolicy viewer (KON-476): which pods a policy applies to, evaluated against the pods that
/// are there, and its rules as sentences rather than nested selectors. Driven off the fake's seed — a
/// default-deny plus one allow for postgres — because that pair is the shape the page exists for.
/// </summary>
public sealed class NetworkPolicyTests
{
    private static async Task<NetworkPolicy> Seeded(FakeClusterEngine cluster, string name) =>
        (await cluster.ListNetworkPoliciesAsync("app")).Single(n => n.Name == name);

    private static async Task Eventually(Func<bool> done)
    {
        for (var i = 0; i < 100 && !done(); i++)
            await Task.Delay(20);
    }

    [Fact]
    public async Task The_list_shows_what_each_policy_applies_to_and_isolates()
    {
        using var page = new ClusterNetworkPoliciesViewModel(new FakeClusterEngine(), "app");
        await page.LoadAsync();

        var deny = page.Items.Single(r => r.Name == "default-deny-ingress");
        Assert.Equal("all pods", deny.AppliesTo);
        Assert.Equal("Ingress", deny.Isolates);

        var allow = page.Items.Single(r => r.Name == "postgres-from-api");
        Assert.Equal("app=postgres", allow.AppliesTo);
        Assert.Equal("Ingress, Egress", allow.Isolates);
    }

    [Fact]
    public async Task The_detail_lists_the_pods_its_selector_matches_right_now()
    {
        var cluster = new FakeClusterEngine();
        using var detail = new ClusterNetworkPolicyDetailViewModel(cluster, await Seeded(cluster, "postgres-from-api"));
        await Eventually(() => !detail.PodsLoading);

        Assert.Equal("Applies to", detail.PodsTabLabel);
        Assert.Equal(["postgres-0"], detail.Pods.Select(p => p.Name));
    }

    [Fact]
    public async Task An_empty_selector_applies_to_every_pod_in_the_namespace()
    {
        var cluster = new FakeClusterEngine();
        using var detail = new ClusterNetworkPolicyDetailViewModel(cluster, await Seeded(cluster, "default-deny-ingress"));
        await Eventually(() => !detail.PodsLoading);

        var inApp = (await cluster.ListPodsAsync("app")).Select(p => p.Name).Order();
        Assert.Equal(inApp, detail.Pods.Select(p => p.Name).Order());
        Assert.Equal("all pods in this namespace", detail.AppliesToText);
    }

    [Fact]
    public async Task An_isolated_direction_without_rules_is_called_a_deny_all()
    {
        var cluster = new FakeClusterEngine();
        using var detail = new ClusterNetworkPolicyDetailViewModel(cluster, await Seeded(cluster, "default-deny-ingress"));

        Assert.False(detail.HasIngressRules);
        Assert.Contains("denied", detail.IngressNote);
        // Egress is simply not this policy's business, which is a different sentence.
        Assert.Contains("does not restrict", detail.EgressNote);
    }

    [Fact]
    public async Task Rules_read_as_peers_and_ports()
    {
        var cluster = new FakeClusterEngine();
        using var detail = new ClusterNetworkPolicyDetailViewModel(cluster, await Seeded(cluster, "postgres-from-api"));

        Assert.Null(detail.IngressNote);
        var from = Assert.Single(detail.IngressRules);
        Assert.Equal("Pods matching app in (api, migrate) in this namespace", from.Peers);
        Assert.Equal("TCP 5432", from.Ports);

        Assert.Equal(
            ["All pods in namespaces matching kubernetes.io/metadata.name=kube-system", "10.0.0.0/8 except 10.0.99.0/24"],
            detail.EgressRules.Select(r => r.Peers));
        Assert.Equal(["UDP 53", "All ports"], detail.EgressRules.Select(r => r.Ports));
    }

    [Fact]
    public void An_empty_namespace_selector_opens_the_peer_to_every_namespace()
    {
        // No namespace selector and an empty one look alike in YAML and are nothing alike on the wire.
        var row = new NetworkPolicyRuleRow(new NetworkPolicyRule
        {
            Peers = [new NetworkPolicyPeer { PodSelector = new LabelSelector(), NamespaceSelector = new LabelSelector() }],
            Ports = [new NetworkPolicyPort("TCP", "8000", 8080)],
        });

        Assert.Equal("All pods in all namespaces", row.Peers);
        Assert.Equal("TCP 8000–8080", row.Ports);
        Assert.Equal("Anywhere", new NetworkPolicyRuleRow(new NetworkPolicyRule()).Peers);
    }
}
