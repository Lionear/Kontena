using System.Text;
using Kontena.Core;
using Kontena.Core.Models;
using Kontena.Core.Orchestration;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Core.Orchestration.Models;
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
    public async Task DryRun_apply_reports_would_change_without_persisting()
    {
        var cluster = NewCluster();
        var bundle = new ManifestBundle { Yaml = "kind: Deployment\n---\nkind: Service", DryRun = true };

        var results = new List<ApplyProgress>();
        await foreach (var p in cluster.ApplyAsync(bundle))
            results.Add(p);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(ApplyAction.WouldChange, r.Action));
        Assert.All(results, r => Assert.NotEmpty(r.Diff));
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
