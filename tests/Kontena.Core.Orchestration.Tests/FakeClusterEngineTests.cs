using System.Text;
using Kontena.Sdk;
using Kontena.Sdk.Models;
using Kontena.Sdk.Orchestration;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;
using Xunit;

namespace Kontena.Core.Orchestration.Tests;

public class FakeClusterEngineTests
{
    private static FakeClusterEngine NewCluster() => new();

    [Fact]
    public void Is_a_backend_and_reports_its_id()
    {
        var backend = Assert.IsAssignableFrom<IBackend>(NewCluster());
        Assert.Equal("kubernetes", backend.Backend);
    }

    [Fact]
    public void Capabilities_advertise_expected_flags()
    {
        var caps = NewCluster().Capabilities;
        Assert.True(caps.Apply);
        Assert.True(caps.Watch);
        Assert.True(caps.Metrics);
        Assert.True(caps.Exec);
    }

    [Fact]
    public async Task GetInfo_returns_a_ClusterInfo_with_node_count()
    {
        var cluster = NewCluster();
        var info = await cluster.GetInfoAsync();

        var clusterInfo = Assert.IsType<ClusterInfo>(info);
        Assert.Equal("Kubernetes", clusterInfo.Kind);
        Assert.Equal("GKE", clusterInfo.Distribution);

        var nodes = await cluster.ListNodesAsync();
        Assert.Equal(nodes.Count, clusterInfo.NodeCount);
    }

    [Fact]
    public async Task Ping_does_not_throw()
    {
        await NewCluster().PingAsync();
    }

    [Fact]
    public async Task Lists_seeded_resources()
    {
        var cluster = NewCluster();
        Assert.NotEmpty(await cluster.ListNamespacesAsync());
        Assert.NotEmpty(await cluster.ListNodesAsync());
        Assert.NotEmpty(await cluster.ListWorkloadsAsync());
        Assert.NotEmpty(await cluster.ListPodsAsync());
        Assert.NotEmpty(await cluster.ListServicesAsync());
        Assert.NotEmpty(await cluster.ListIngressesAsync());
        Assert.NotEmpty(await cluster.ListPvcsAsync());
    }

    [Fact]
    public async Task Listers_filter_by_namespace()
    {
        var cluster = NewCluster();
        var app = await cluster.ListPodsAsync("app");
        Assert.All(app, p => Assert.Equal("app", p.Namespace));

        var none = await cluster.ListPodsAsync("does-not-exist");
        Assert.Empty(none);
    }

    [Fact]
    public async Task Workloads_filter_by_kind()
    {
        var cluster = NewCluster();
        var statefulSets = await cluster.ListWorkloadsAsync(WorkloadKind.StatefulSet);
        Assert.All(statefulSets, w => Assert.Equal(WorkloadKind.StatefulSet, w.Kind));
        Assert.NotEmpty(statefulSets);
    }

    [Fact]
    public async Task UseContext_switches_the_current_context()
    {
        var cluster = NewCluster();
        await cluster.UseContextAsync("minikube");

        Assert.True(cluster.Contexts.Single(c => c.Name == "minikube").IsCurrent);
        Assert.False(cluster.Contexts.Single(c => c.Name == "prod-eu-west").IsCurrent);

        var info = await cluster.GetInfoAsync();
        Assert.Equal("minikube", info.DisplayName);
    }

    [Fact]
    public async Task UseContext_rejects_unknown_context()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => NewCluster().UseContextAsync("nope").AsTask());
    }

    [Fact]
    public async Task Nodes_report_conditions_and_one_is_seeded_under_pressure()
    {
        var nodes = await NewCluster().ListNodesAsync();

        // Conditions come off the node status, so every node has them with or without metrics.
        Assert.All(nodes, n =>
        {
            Assert.Contains(n.Conditions, c => c.Type == "Ready");
            Assert.Contains(n.Conditions, c => c.Type == "DiskPressure");
        });

        // One node is deliberately unhealthy so the Nodes view's indicators have something to show.
        var pressured = Assert.Single(nodes, n => n.Problems.Count > 0);
        Assert.Equal("DiskPressure", Assert.Single(pressured.Problems).Type);
        Assert.Equal("Ready", pressured.Status);
    }

    // ── Declarative core (KON-69) ────────────────────────────────────────────

    /// <summary>The seeded "api" Deployment, scaled from 3 to 5 and moved to a newer image.</summary>
    private const string ChangedApiDeployment = """
        apiVersion: apps/v1
        kind: Deployment
        metadata:
          name: api
          namespace: app
        spec:
          replicas: 5
          selector:
            matchLabels: {app: api}
          template:
            spec:
              containers:
                - name: api
                  image: ghcr.io/lionear/api:2.0
        """;

    private static async Task<List<ApplyProgress>> ApplyAsync(FakeClusterEngine cluster, string yaml, bool dryRun)
    {
        var results = new List<ApplyProgress>();
        await foreach (var p in cluster.ApplyAsync(new ManifestBundle { Yaml = yaml, DryRun = dryRun }))
            results.Add(p);

        return results;
    }

    [Fact]
    public async Task DryRun_apply_diffs_against_the_live_resource_without_persisting()
    {
        var cluster = NewCluster();

        var results = await ApplyAsync(cluster, ChangedApiDeployment, dryRun: true);

        var result = Assert.Single(results);
        Assert.Equal(ApplyAction.WouldChange, result.Action);
        Assert.Equal("api", result.Resource.Name);
        Assert.Equal("app", result.Resource.Namespace);
        Assert.Contains("-  replicas: 3", result.Diff, StringComparison.Ordinal);
        Assert.Contains("+  replicas: 5", result.Diff, StringComparison.Ordinal);
        Assert.Contains("+          image: ghcr.io/lionear/api:2.0", result.Diff, StringComparison.Ordinal);

        // Nothing was persisted.
        var workload = Assert.Single(await cluster.ListWorkloadsAsync(WorkloadKind.Deployment, "app"), w => w.Name == "api");
        Assert.Equal(3, workload.Desired);
    }

    [Fact]
    public async Task Apply_persists_the_change()
    {
        var cluster = NewCluster();

        var result = Assert.Single(await ApplyAsync(cluster, ChangedApiDeployment, dryRun: false));
        Assert.Equal(ApplyAction.Configured, result.Action);

        var workload = Assert.Single(await cluster.ListWorkloadsAsync(WorkloadKind.Deployment, "app"), w => w.Name == "api");
        Assert.Equal(5, workload.Desired);
        Assert.Equal(["ghcr.io/lionear/api:2.0"], workload.Images);
        Assert.Equal(RolloutStatus.Progressing, workload.RolloutStatus);
    }

    [Fact]
    public async Task Applying_the_live_manifest_unchanged_is_a_no_op()
    {
        var cluster = NewCluster();
        var live = await cluster.GetManifestAsync(new ResourceRef(GroupVersionKind.Deployment, "app", "api"));

        var result = Assert.Single(await ApplyAsync(cluster, live, dryRun: false));

        Assert.Equal(ApplyAction.Unchanged, result.Action);
        Assert.Empty(result.Diff);
    }

    [Fact]
    public async Task Apply_creates_a_resource_that_does_not_exist_yet()
    {
        var cluster = NewCluster();
        const string yaml = """
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: payments-worker
              namespace: app
            spec:
              replicas: 2
              template:
                spec:
                  containers:
                    - name: worker
                      image: payments/worker:1.8.4
            """;

        var dry = Assert.Single(await ApplyAsync(cluster, yaml, dryRun: true));
        Assert.Equal(ApplyAction.WouldCreate, dry.Action);
        Assert.DoesNotContain(await cluster.ListWorkloadsAsync(null, "app"), w => w.Name == "payments-worker");

        var applied = Assert.Single(await ApplyAsync(cluster, yaml, dryRun: false));
        Assert.Equal(ApplyAction.Created, applied.Action);

        var created = Assert.Single(await cluster.ListWorkloadsAsync(null, "app"), w => w.Name == "payments-worker");
        Assert.Equal(WorkloadKind.Deployment, created.Kind);
        Assert.Equal(2, created.Desired);
        Assert.Equal(["payments/worker:1.8.4"], created.Images);
    }

    [Fact]
    public async Task Apply_streams_one_result_per_document()
    {
        var cluster = NewCluster();
        var bundle = $"{ChangedApiDeployment}\n---\napiVersion: v1\nkind: Namespace\nmetadata:\n  name: payments\n";

        var results = await ApplyAsync(cluster, bundle, dryRun: true);

        Assert.Equal(2, results.Count);
        Assert.Equal(ApplyAction.WouldChange, results[0].Action);
        Assert.Equal(ApplyAction.WouldCreate, results[1].Action);
        Assert.Equal("payments", results[1].Resource.Name);
    }

    [Fact]
    public async Task Apply_reports_an_unparsable_document_as_failed()
    {
        var cluster = NewCluster();

        var result = Assert.Single(await ApplyAsync(cluster, "kind: Deployment\nmetadata:\n  name: nope\n", dryRun: true));

        Assert.Equal(ApplyAction.Failed, result.Action);
        Assert.Contains("apiVersion", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_is_idempotent_for_kinds_the_fake_does_not_model()
    {
        var cluster = NewCluster();
        const string yaml = """
            apiVersion: autoscaling/v2
            kind: HorizontalPodAutoscaler
            metadata:
              name: api
              namespace: app
            spec:
              minReplicas: 2
            """;

        Assert.Equal(ApplyAction.Created, Assert.Single(await ApplyAsync(cluster, yaml, dryRun: false)).Action);
        Assert.Equal(ApplyAction.Unchanged, Assert.Single(await ApplyAsync(cluster, yaml, dryRun: false)).Action);
    }

    [Fact]
    public async Task Delete_removes_a_workload_and_the_pods_it_owns()
    {
        var cluster = NewCluster();
        var reference = new ResourceRef(GroupVersionKind.Deployment, "app", "api");

        await cluster.DeleteAsync(reference);

        Assert.DoesNotContain(await cluster.ListWorkloadsAsync(null, "app"), w => w.Name == "api");
        Assert.DoesNotContain(await cluster.ListPodsAsync("app"), p => p.ControlledBy == "Deployment/api");
        Assert.StartsWith("#", await cluster.GetManifestAsync(reference), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_removes_a_pod()
    {
        var cluster = NewCluster();

        await cluster.DeleteAsync(new ResourceRef(GroupVersionKind.Pod, "app", "web-5f2a"));

        Assert.DoesNotContain(await cluster.ListPodsAsync("app"), p => p.Name == "web-5f2a");
    }

    [Fact]
    public async Task Watch_yields_seeded_pods_then_completes()
    {
        var cluster = NewCluster();
        var events = new List<ResourceEvent>();
        await foreach (var e in cluster.WatchAsync(GroupVersionKind.Pod, "app"))
            events.Add(e);

        Assert.NotEmpty(events);
        Assert.All(events, e => Assert.Equal(WatchEventType.Added, e.Type));
        Assert.All(events, e => Assert.Equal("Pod", e.Resource.Kind.Kind));
    }

    [Fact]
    public async Task Scale_updates_the_workload_desired_count()
    {
        var cluster = NewCluster();
        var api = new ResourceRef(GroupVersionKind.Deployment, "app", "api");

        await cluster.ScaleAsync(api, 5);

        var workload = (await cluster.ListWorkloadsAsync()).Single(w => w.Name == "api" && w.Namespace == "app");
        Assert.Equal(5, workload.Desired);
    }

    [Fact]
    public async Task StreamLogs_yields_lines()
    {
        var cluster = NewCluster();
        var pod = new ResourceRef(GroupVersionKind.Pod, "app", "api-7d9c");

        var lines = new List<LogEntry>();
        await foreach (var line in cluster.StreamLogsAsync(pod, "api", follow: false))
            lines.Add(line);

        Assert.NotEmpty(lines);
    }

    [Fact]
    public async Task StreamMetrics_yields_samples_when_metrics_supported()
    {
        var cluster = NewCluster();
        var pod = new ResourceRef(GroupVersionKind.Pod, "app", "api-7d9c");

        var samples = new List<PodMetrics>();
        await foreach (var m in cluster.StreamMetricsAsync(pod))
            samples.Add(m);

        Assert.NotEmpty(samples);
        Assert.All(samples, m => Assert.Equal("api-7d9c", m.Pod));
    }

    [Fact]
    public async Task Exec_session_echoes_and_exits()
    {
        var cluster = NewCluster();
        var pod = new ResourceRef(GroupVersionKind.Pod, "app", "api-7d9c");

        await using var session = await cluster.StartExecSessionAsync(pod, "api", new ExecRequest { Command = ["/bin/sh"] });

        // Drain output on a background task; write "exit" to end the session.
        var output = new StringBuilder();
        var reader = Task.Run(async () =>
        {
            await foreach (var chunk in session.ReadOutputAsync())
                output.Append(Encoding.UTF8.GetString(chunk.Span));
        });

        await session.WriteAsync(Encoding.UTF8.GetBytes("exit"));
        await reader;

        Assert.Equal(0, session.ExitCode);
        Assert.Contains("api-7d9c", output.ToString());
    }

    [Fact]
    public async Task PortForward_returns_an_active_handle()
    {
        var cluster = NewCluster();
        var svc = new ResourceRef(GroupVersionKind.Service, "app", "web");

        await using var pf = await cluster.PortForwardAsync(svc, 80, 8080);
        Assert.True(pf.IsActive);
        Assert.Equal(8080, pf.LocalPort);
        Assert.Equal(80, pf.RemotePort);
    }

    [Fact]
    public async Task GetManifest_returns_yaml_for_the_ref()
    {
        var cluster = NewCluster();
        var yaml = await cluster.GetManifestAsync(new ResourceRef(GroupVersionKind.Deployment, "app", "api"));

        Assert.Contains("kind: Deployment", yaml);
        Assert.Contains("name: api", yaml);
        Assert.Contains("namespace: app", yaml);
    }
}
