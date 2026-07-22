using Kontena.Adapters.Kubernetes;
using Kontena.Core.Orchestration;
using Kontena.Core.Orchestration.Models;

namespace Kontena.Adapters.Kubernetes.Tests;

/// <summary>
/// The composition rules are the risky part of KON-94: neither source is complete on its own, so
/// which one answers for which field decides whether a gauge is right, wrong, or missing. These use
/// stub sources, so no cluster is involved.
/// </summary>
public class ClusterMetricsTests
{
    private sealed class StubSource(string name, bool available) : IMetricsSource
    {
        public string Name => name;
        public bool IsAvailable { get; private set; } = available;

        public IReadOnlyDictionary<string, NodeUsage> Usage { get; init; } = new Dictionary<string, NodeUsage>();
        public IReadOnlyList<PodMetrics> Pods { get; init; } = [];
        public int PodCallCount { get; private set; }
        public int ProbeCount { get; private set; }

        public ValueTask<bool> ProbeAsync(CancellationToken ct = default)
        {
            ProbeCount++;
            return ValueTask.FromResult(IsAvailable);
        }

        public ValueTask<IReadOnlyDictionary<string, NodeUsage>> GetNodeUsageAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(Usage);

        public ValueTask<IReadOnlyList<PodMetrics>> GetPodUsageAsync(string? ns = null, CancellationToken ct = default)
        {
            PodCallCount++;
            return ValueTask.FromResult(Pods);
        }
    }

    private sealed class StubDiskSource(string name, bool available) : INodeDiskSource
    {
        private readonly StubSource _inner = new(name, available);

        public string Name => _inner.Name;
        public bool IsAvailable => _inner.IsAvailable;

        public IReadOnlyDictionary<string, NodeUsage> Usage { get; init; } = new Dictionary<string, NodeUsage>();
        public IReadOnlyDictionary<string, long> Capacity { get; init; } = new Dictionary<string, long>();
        public IReadOnlyList<PodMetrics> Pods { get; init; } = [];
        public int PodCallCount { get; private set; }

        public ValueTask<bool> ProbeAsync(CancellationToken ct = default) => _inner.ProbeAsync(ct);

        public ValueTask<IReadOnlyDictionary<string, NodeUsage>> GetNodeUsageAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(Usage);

        public ValueTask<IReadOnlyDictionary<string, long>> GetNodeDiskCapacityAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(Capacity);

        public ValueTask<IReadOnlyList<PodMetrics>> GetPodUsageAsync(string? ns = null, CancellationToken ct = default)
        {
            PodCallCount++;
            return ValueTask.FromResult(Pods);
        }
    }

    private static StubSource MetricsServer(bool available, long cpu = 500, long memory = 1024) => new("metrics-server", available)
    {
        Usage = new Dictionary<string, NodeUsage>
        {
            ["worker-1"] = new() { CpuMillicores = cpu, MemoryBytes = memory },
        },
    };

    private static StubDiskSource Kubelet(bool available, long cpu = 111, long memory = 222, long disk = 800) => new("kubelet", available)
    {
        Usage = new Dictionary<string, NodeUsage>
        {
            ["worker-1"] = new() { CpuMillicores = cpu, MemoryBytes = memory, DiskUsedBytes = disk },
        },
        Capacity = new Dictionary<string, long> { ["worker-1"] = 1000 },
    };

    [Theory]
    [InlineData(true, true, "metrics-server + kubelet")]
    [InlineData(true, false, "metrics-server")]
    [InlineData(false, true, "kubelet")]
    [InlineData(false, false, "none")]
    public async Task Name_reports_which_sources_actually_answered(bool server, bool kubelet, string expected)
    {
        var metrics = new ClusterMetrics(MetricsServer(server), Kubelet(kubelet));
        await metrics.ProbeAsync();

        Assert.Equal(expected, metrics.Name);
        Assert.Equal(server || kubelet, metrics.IsAvailable);
    }

    [Fact]
    public async Task With_both_sources_cpu_and_memory_come_from_metrics_server_and_disk_from_the_kubelet()
    {
        // The metrics-server is cluster-wide and consistent, so it wins for CPU/memory; it reports
        // no disk at all, so that has to come from the kubelet or not at all.
        var metrics = new ClusterMetrics(MetricsServer(true, cpu: 500, memory: 1024), Kubelet(true, cpu: 111, memory: 222, disk: 800));
        await metrics.ProbeAsync();

        var usage = (await metrics.GetNodeUsageAsync())["worker-1"];

        Assert.Equal(500, usage.CpuMillicores);
        Assert.Equal(1024, usage.MemoryBytes);
        Assert.Equal(800, usage.DiskUsedBytes);
    }

    [Fact]
    public async Task Without_a_metrics_server_the_kubelet_supplies_everything()
    {
        var metrics = new ClusterMetrics(MetricsServer(false), Kubelet(true, cpu: 111, memory: 222, disk: 800));
        await metrics.ProbeAsync();

        var usage = (await metrics.GetNodeUsageAsync())["worker-1"];

        Assert.Equal(111, usage.CpuMillicores);
        Assert.Equal(222, usage.MemoryBytes);
        Assert.Equal(800, usage.DiskUsedBytes);
        Assert.True(metrics.HasDisk);
    }

    [Fact]
    public async Task Without_the_kubelet_there_is_no_disk_but_cpu_and_memory_still_work()
    {
        // The managed-cluster case: metrics-server present, nodes/proxy denied.
        var metrics = new ClusterMetrics(MetricsServer(true), Kubelet(false));
        await metrics.ProbeAsync();

        var usage = (await metrics.GetNodeUsageAsync())["worker-1"];

        Assert.Equal(500, usage.CpuMillicores);
        Assert.Null(usage.DiskUsedBytes);
        Assert.False(metrics.HasDisk);
        Assert.Empty(await metrics.GetNodeDiskCapacityAsync());
    }

    [Fact]
    public async Task With_neither_source_nothing_is_reported_and_nothing_throws()
    {
        var metrics = new ClusterMetrics(MetricsServer(false), Kubelet(false));
        await metrics.ProbeAsync();

        Assert.False(metrics.IsAvailable);
        Assert.False(metrics.HasDisk);
        Assert.Empty(await metrics.GetNodeUsageAsync());
        Assert.Empty(await metrics.GetNodeDiskCapacityAsync());
        Assert.Empty(await metrics.GetPodUsageAsync());
    }

    [Fact]
    public async Task Pod_usage_prefers_the_metrics_server_and_falls_back_to_the_kubelet()
    {
        var server = MetricsServer(true);
        var kubelet = Kubelet(true);
        var withServer = new ClusterMetrics(server, kubelet);
        await withServer.ProbeAsync();
        await withServer.GetPodUsageAsync();

        Assert.Equal(1, server.PodCallCount);
        Assert.Equal(0, kubelet.PodCallCount);

        var fallbackKubelet = Kubelet(true);
        var withoutServer = new ClusterMetrics(MetricsServer(false), fallbackKubelet);
        await withoutServer.ProbeAsync();
        await withoutServer.GetPodUsageAsync();

        Assert.Equal(1, fallbackKubelet.PodCallCount);
    }

    [Fact]
    public async Task Probing_happens_once_even_when_the_caller_never_pings()
    {
        // Callers must not have to know to probe first — the UI calling order is not a contract.
        var server = MetricsServer(true);
        var metrics = new ClusterMetrics(server, Kubelet(true));

        await metrics.GetNodeUsageAsync();
        await metrics.GetNodeUsageAsync();
        await metrics.GetPodUsageAsync();

        Assert.Equal(1, server.ProbeCount);
    }
}
