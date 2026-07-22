using System.Globalization;
using System.Text;
using Kontena.Adapters.Kubernetes;
using Kontena.Core;
using Kontena.Core.Models;
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
        Assert.True(engine.Capabilities.Apply);
        Assert.True(engine.Capabilities.Exec);
        Assert.True(engine.Capabilities.PortForward);

        // Helm is the one thing left; the UI must be told so it hides that affordance.
        Assert.False(engine.Capabilities.Helm);
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

    // ── Exec and port-forward (KON-97) ───────────────────────────────────────

    /// <summary>
    /// A running pod with a shell to exec into. kube-system always has one, so these tests need no
    /// fixture of their own — and read-only commands cannot disturb it.
    /// </summary>
    private static async Task<(ResourceRef Pod, string Container)> AnyRunningPodAsync(KubernetesClusterEngine engine)
    {
        var pod = (await engine.ListPodsAsync("kube-system"))
            .FirstOrDefault(p => p.Phase == PodPhase.Running && p.Containers.Count > 0 && p.ReadyContainers > 0);

        Skip.If(pod is null, "No running pod in kube-system to exec into.");
        return (new ResourceRef(GroupVersionKind.Pod, pod!.Namespace, pod.Name), pod.Containers[0].Name);
    }

    [SkippableFact]
    public async Task Exec_runs_a_command_and_streams_its_output()
    {
        using var engine = await RequireClusterAsync();
        var (pod, container) = await AnyRunningPodAsync(engine);

        await using var session = await engine.StartExecSessionAsync(
            pod, container, new ExecRequest { Command = ["/bin/sh"], Tty = true });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var output = new StringBuilder();
        var reader = Task.Run(
            async () =>
            {
                try
                {
                    await foreach (var chunk in session.ReadOutputAsync(cts.Token))
                    {
                        output.Append(Encoding.UTF8.GetString(chunk.Span));
                        if (output.ToString().Contains("KONTENA-EXEC-OK", StringComparison.Ordinal))
                            return;
                    }
                }
                catch (OperationCanceledException) { /* window closed; assert on what arrived */ }
            },
            cts.Token);

        await Task.Delay(500, cts.Token);
        await session.ResizeAsync(100, 30, cts.Token);
        await session.WriteAsync(Encoding.UTF8.GetBytes("echo KONTENA-EXEC-OK\n"), cts.Token);
        await Task.WhenAny(reader, Task.Delay(TimeSpan.FromSeconds(15), CancellationToken.None));

        Assert.Contains("KONTENA-EXEC-OK", output.ToString(), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Port_forward_binds_a_local_port_and_frees_it_again()
    {
        using var engine = await RequireClusterAsync();
        var (pod, _) = await AnyRunningPodAsync(engine);

        int bound;
        await using (var tunnel = await engine.PortForwardAsync(pod, 80))
        {
            Assert.True(tunnel.IsActive);
            Assert.Equal(80, tunnel.RemotePort);
            Assert.True(tunnel.LocalPort > 0);
            bound = tunnel.LocalPort;
        }

        // Disposal must actually release the listener, or ports leak for the rest of the session.
        await using var reused = await engine.PortForwardAsync(pod, 80, bound);
        Assert.Equal(bound, reused.LocalPort);
    }

    [SkippableFact]
    public async Task A_local_port_already_in_use_is_reported_not_swallowed()
    {
        using var engine = await RequireClusterAsync();
        var (pod, _) = await AnyRunningPodAsync(engine);

        await using var held = await engine.PortForwardAsync(pod, 80, 18096);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.PortForwardAsync(pod, 80, 18096).AsTask());
        Assert.Contains("18096", error.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Forwarding_a_service_with_no_ready_pod_says_so()
    {
        using var engine = await RequireClusterAsync();

        // "kubernetes" in default is a service with no selector — it cannot resolve to a pod.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.PortForwardAsync(
                new ResourceRef(GroupVersionKind.Service, "default", "kubernetes"), 443).AsTask());

        Assert.Contains("kubernetes", error.Message, StringComparison.Ordinal);
    }

    // ── Apply / dry-run (KON-86) ─────────────────────────────────────────────

    /// <summary>
    /// A namespace per test, so a failed run cannot disturb anything real and — just as important —
    /// so one test's cleanup cannot make the next one skip: namespace deletion is asynchronous, and
    /// a shared name would still be Terminating when the next test looks.
    /// </summary>
    private const string NamespacePrefix = "kontena-apply-test";

    private static string Bundle(string ns, int replicas = 1, string image = "nginx:1.27-alpine") =>
        "apiVersion: v1\nkind: Namespace\nmetadata:\n  name: " + ns + "\n" +
        "---\n" +
        "apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: probe\n  namespace: " + ns + "\n" +
        "spec:\n  replicas: " + replicas.ToString(CultureInfo.InvariantCulture) + "\n" +
        "  selector:\n    matchLabels:\n      app: probe\n" +
        "  template:\n    metadata:\n      labels:\n        app: probe\n" +
        "    spec:\n      containers:\n        - name: probe\n          image: " + image + "\n";

    private static async Task<List<ApplyProgress>> ApplyAsync(
        KubernetesClusterEngine engine, string yaml, bool dryRun)
    {
        var results = new List<ApplyProgress>();
        await foreach (var p in engine.ApplyAsync(new ManifestBundle { Yaml = yaml, DryRun = dryRun }))
            results.Add(p);

        return results;
    }

    /// <summary>Claim a namespace name for one test, refusing if something already owns it.</summary>
    private static async Task<string> ClaimNamespaceAsync(KubernetesClusterEngine engine, string suffix)
    {
        var ns = $"{NamespacePrefix}-{suffix}";
        var existing = (await engine.ListNamespacesAsync()).Any(n => n.Name == ns);
        Skip.If(existing, $"Namespace {ns} already exists; refusing to touch it.");
        return ns;
    }

    [SkippableFact]
    public async Task Dry_run_reports_a_create_without_persisting_anything()
    {
        using var engine = await RequireClusterAsync();
        var ns = await ClaimNamespaceAsync(engine, "dryrun");

        var results = await ApplyAsync(engine, Bundle(ns), dryRun: true);

        Assert.Equal(2, results.Count);
        Assert.Equal(ApplyAction.WouldCreate, results[0].Action);

        // A server-side dry-run persists nothing, so the namespace document 1 would create does not
        // exist when document 2 is validated — kubectl behaves the same way. The adapter explains
        // that rather than passing on a bare "not found".
        Assert.Equal(ApplyAction.Failed, results[1].Action);
        Assert.Contains("this bundle creates it", results[1].Error, StringComparison.Ordinal);

        // The whole point of a dry-run: the cluster is untouched.
        Assert.DoesNotContain(await engine.ListNamespacesAsync(), n => n.Name == ns);
    }

    [SkippableFact]
    public async Task Apply_creates_then_reports_unchanged_then_diffs_a_real_change()
    {
        using var engine = await RequireClusterAsync();
        var ns = await ClaimNamespaceAsync(engine, "apply");

        try
        {
            var created = await ApplyAsync(engine, Bundle(ns), dryRun: false);
            Assert.All(created, r => Assert.Equal(ApplyAction.Created, r.Action));

            // Re-applying the same bundle must be a no-op, not a perpetual "configured".
            var again = await ApplyAsync(engine, Bundle(ns), dryRun: true);
            Assert.All(again, r => Assert.Equal(ApplyAction.Unchanged, r.Action));

            var changed = await ApplyAsync(engine, Bundle(ns, replicas: 3, image: "nginx:1.29-alpine"), dryRun: true);
            var deployment = Assert.Single(changed, r => r.Resource.Kind.Kind == "Deployment");
            Assert.Equal(ApplyAction.WouldChange, deployment.Action);
            Assert.Contains("-  replicas: 1", deployment.Diff, StringComparison.Ordinal);
            Assert.Contains("+  replicas: 3", deployment.Diff, StringComparison.Ordinal);
            Assert.Contains("nginx:1.29-alpine", deployment.Diff, StringComparison.Ordinal);
        }
        finally
        {
            await engine.DeleteAsync(new ResourceRef(GroupVersionKind.Namespace, null, ns));
        }
    }

    [SkippableFact]
    public async Task A_manifest_the_api_server_rejects_surfaces_its_reason()
    {
        using var engine = await RequireClusterAsync();

        // Server-side dry-run runs real validation, so the message comes from the cluster itself.
        var result = Assert.Single(await ApplyAsync(engine,
            "apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: bad\n  namespace: default\nspec:\n  replicas: -3\n",
            dryRun: true));

        Assert.Equal(ApplyAction.Failed, result.Action);
        Assert.Contains("replicas", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task An_unserved_kind_is_reported_per_resource_not_thrown()
    {
        using var engine = await RequireClusterAsync();

        var result = Assert.Single(await ApplyAsync(engine,
            "apiVersion: nope.example.com/v1\nkind: Nonesuch\nmetadata:\n  name: x\n  namespace: default\n",
            dryRun: true));

        Assert.Equal(ApplyAction.Failed, result.Action);
        Assert.Contains("does not serve", result.Error, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task A_document_without_a_kind_fails_that_document_only()
    {
        using var engine = await RequireClusterAsync();

        var results = await ApplyAsync(engine,
            "metadata:\n  name: x\n---\napiVersion: v1\nkind: Namespace\nmetadata:\n  name: default\n",
            dryRun: true);

        Assert.Equal(2, results.Count);
        Assert.Equal(ApplyAction.Failed, results[0].Action);
        Assert.Equal(ApplyAction.Unchanged, results[1].Action);
    }
}
