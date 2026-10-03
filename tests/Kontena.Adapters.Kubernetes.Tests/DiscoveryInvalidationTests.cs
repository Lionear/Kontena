using System.Net;
using System.Net.Http;
using k8s;
using Kontena.Adapters.Kubernetes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Adapters.Kubernetes.Tests;

/// <summary>
/// A CRD event forgets its group's cached discovery (KON-488). A definition installed outside Kontena
/// into a group already read this session would otherwise stay a "no such kind" until a reconnect —
/// the watch would announce it and the read it prompted would answer from before it existed.
/// </summary>
public sealed class DiscoveryInvalidationTests
{
    private static readonly GroupVersionKind Rule = new("monitoring.coreos.com", "v1", "PrometheusRule");

    /// <summary>Serves one group/version's resource list, and counts how often it was asked.</summary>
    private sealed class Discovery : DelegatingHandler
    {
        public int Asked;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Asked);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{ "groupVersion": "monitoring.coreos.com/v1", "resources": [ { "name": "prometheusrules", "kind": "PrometheusRule", "namespaced": true, "verbs": ["list"] } ] }""",
                    System.Text.Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
        }
    }

    [Fact]
    public async Task Forgetting_a_group_makes_the_next_resolve_ask_again_and_leaves_other_groups_cached()
    {
        var server = new Discovery();
        using var client = new k8s.Kubernetes(new KubernetesClientConfiguration { Host = "https://10.0.0.1:6443/" }, server);
        var resolver = new ApiResourceResolver(client);

        Assert.NotNull(await resolver.ResolveAsync(Rule));
        await resolver.ResolveAsync(Rule);
        Assert.Equal(1, server.Asked);

        resolver.InvalidateGroup("cert-manager.io");
        await resolver.ResolveAsync(Rule);
        Assert.Equal(1, server.Asked);

        resolver.InvalidateGroup("monitoring.coreos.com");
        await resolver.ResolveAsync(Rule);
        Assert.Equal(2, server.Asked);
    }
}
