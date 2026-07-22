using Kontena.Adapters.Kubernetes;
using Kontena.Core;
using Kontena.Core.Orchestration;
using Kontena.Core.Orchestration.Models;

namespace Kontena.Adapters.Kubernetes.Tests;

/// <summary>Provider wiring and the null metrics source — no cluster needed.</summary>
public class KubernetesClusterProviderTests
{
    [Fact]
    public void Provider_describes_a_cluster_backend_scoped_to_its_context()
    {
        var provider = new KubernetesClusterProvider("prod-eu-west");

        Assert.Equal("kubernetes:prod-eu-west", provider.Backend);
        Assert.Equal("prod-eu-west", provider.DisplayName);
        Assert.Equal(BackendKind.Cluster, provider.Kind);
    }

    [Theory]
    [InlineData("kind-kind", "KIND")]
    [InlineData("gke_prod_eu-west1", "GKE")]
    [InlineData("minikube", "MIN")]
    [InlineData("k3s", "K3S")]
    [InlineData("", "K8S")]
    public void Chip_is_a_short_legible_stand_in_for_the_context(string context, string expected)
    {
        Assert.Equal(expected, new KubernetesClusterProvider(context).Chip);
    }

    [Fact]
    public void Discovery_never_throws_without_a_kubeconfig()
    {
        // A machine that only runs containers should simply show no Clusters group.
        var providers = KubernetesClusterProvider.DiscoverAll();

        Assert.NotNull(providers);
    }
}

public class NoMetricsSourceTests
{
    [Fact]
    public async Task Reports_unavailable_and_answers_with_nothing()
    {
        var source = NoMetricsSource.Instance;

        Assert.Equal("none", source.Name);
        Assert.False(source.IsAvailable);
        Assert.False(await source.ProbeAsync());
        Assert.Empty(await source.GetNodeUsageAsync());
        Assert.Empty(await source.GetPodUsageAsync());
    }
}

/// <summary>
/// End-to-end reads against whatever cluster the kubeconfig points at. Skipped when there is no
/// reachable cluster, so CI stays green on a machine without Kubernetes.
/// </summary>
public class KubernetesClusterEngineTests
{
    private static async Task<KubernetesClusterEngine?> ConnectAsync()
    {
        var provider = KubernetesClusterProvider.DiscoverAll().FirstOrDefault();
        if (provider is null)
            return null;

        var engine = (KubernetesClusterEngine)provider.CreateBackend();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await engine.PingAsync(cts.Token);
            return engine;
        }
        catch (Exception)
        {
            engine.Dispose();
            return null;
        }
    }

    private static async Task<KubernetesClusterEngine> RequireClusterAsync()
    {
        var engine = await ConnectAsync();
        Skip.If(engine is null, "No reachable Kubernetes cluster in the kubeconfig.");
        return engine!;
    }

    [SkippableFact]
    public async Task Reports_cluster_identity()
    {
        using var engine = await RequireClusterAsync();

        var info = Assert.IsType<ClusterInfo>(await engine.GetInfoAsync());

        Assert.Equal("Kubernetes", info.Kind);
        Assert.StartsWith("v", info.Version, StringComparison.Ordinal);
        Assert.True(info.NodeCount > 0);
        Assert.NotEmpty(info.Context);
    }

    [SkippableFact]
    public async Task Capabilities_reflect_what_this_cut_actually_does()
    {
        using var engine = await RequireClusterAsync();

        Assert.True(engine.Capabilities.Watch);

        // The mutating half is a later ticket; the UI must be told so it hides those actions.
        Assert.False(engine.Capabilities.Apply);
        Assert.False(engine.Capabilities.Exec);
        Assert.False(engine.Capabilities.PortForward);
    }

    [SkippableFact]
    public async Task Metrics_gate_matches_whether_a_source_answered()
    {
        using var engine = await RequireClusterAsync();

        // Clusters without a metrics-server (kind, plain kubeadm) must degrade, not fail.
        Assert.Equal(engine.Metrics.IsAvailable, engine.Capabilities.Metrics);
    }

    [SkippableFact]
    public async Task Lists_nodes_with_conditions_regardless_of_metrics()
    {
        using var engine = await RequireClusterAsync();

        var nodes = await engine.ListNodesAsync();

        Assert.NotEmpty(nodes);
        Assert.All(nodes, n =>
        {
            Assert.NotEmpty(n.Name);
            Assert.NotEmpty(n.Conditions);
            Assert.Contains(n.Conditions, c => c.Type == "Ready");
            Assert.True(n.Capacity.CpuMillicores > 0);
        });
    }

    [SkippableFact]
    public async Task Lists_namespaces_pods_and_workloads()
    {
        using var engine = await RequireClusterAsync();

        var namespaces = await engine.ListNamespacesAsync();
        Assert.Contains(namespaces, n => n.Name == "kube-system");

        var pods = await engine.ListPodsAsync("kube-system");
        Assert.NotEmpty(pods);
        Assert.All(pods, p => Assert.Equal("kube-system", p.Namespace));

        var workloads = await engine.ListWorkloadsAsync(ns: "kube-system");
        Assert.NotEmpty(workloads);
        Assert.All(workloads, w => Assert.Equal("kube-system", w.Namespace));
    }

    [SkippableFact]
    public async Task Namespace_filter_narrows_the_listing()
    {
        using var engine = await RequireClusterAsync();

        var all = await engine.ListPodsAsync();
        var scoped = await engine.ListPodsAsync("kube-system");

        Assert.True(all.Count >= scoped.Count);
    }

    [SkippableFact]
    public async Task Reads_a_live_manifest()
    {
        using var engine = await RequireClusterAsync();
        var pod = (await engine.ListPodsAsync("kube-system")).FirstOrDefault();
        Skip.If(pod is null, "No pods in kube-system.");

        var yaml = await engine.GetManifestAsync(new ResourceRef(GroupVersionKind.Pod, pod!.Namespace, pod.Name));

        Assert.Contains("kind: Pod", yaml, StringComparison.Ordinal);
        Assert.Contains(pod.Name, yaml, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Watch_seeds_the_informer_with_the_current_world()
    {
        using var engine = await RequireClusterAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var events = new List<ResourceEvent>();
        try
        {
            await foreach (var e in engine.WatchAsync(GroupVersionKind.Pod, "kube-system", cts.Token))
            {
                events.Add(e);
                if (events.Count >= 3)
                    break;
            }
        }
        catch (OperationCanceledException) { /* the window closed — assert on what arrived */ }

        Assert.NotEmpty(events);
        Assert.All(events, e =>
        {
            Assert.Equal("Pod", e.Resource.Kind.Kind);
            Assert.Equal("kube-system", e.Resource.Namespace);
            Assert.False(string.IsNullOrEmpty(e.Manifest));
        });
    }

    [SkippableFact]
    public async Task The_mutating_half_fails_loudly_rather_than_silently()
    {
        using var engine = await RequireClusterAsync();
        var reference = new ResourceRef(GroupVersionKind.Deployment, "default", "nope");

        await Assert.ThrowsAsync<NotSupportedException>(() => engine.ScaleAsync(reference, 2).AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(() => engine.DeleteAsync(reference).AsTask());
        Assert.Throws<NotSupportedException>(() => engine.ApplyAsync(new ManifestBundle { Yaml = "" }));
    }
}
