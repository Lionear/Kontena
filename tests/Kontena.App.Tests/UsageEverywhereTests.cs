using Kontena.App.Controls;
using Kontena.App.ViewModels;
using Kontena.Core.Models;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Engines.Fakes;
using Kontena.Sdk.Models;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// The usage charts on the pages beyond the pod (KON-347): container, node, workload and namespace.
/// What is pinned here is that each page declares the right measures and that its live sampler
/// actually reaches them — the drawing itself is the shared control's, tested once.
/// </summary>
public sealed class UsageEverywhereTests
{
    private static TerminalFont Font() => new("JetBrains Mono", 13, true);

    private static async Task<UsageTrackViewModel> FilledAsync(UsageTrackViewModel usage, int want = 1)
    {
        for (var i = 0; i < 400 && usage.Charts[0].Samples.Count < want; i++)
            await Task.Delay(5);

        return usage;
    }

    // ── Container ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_container_charts_cpu_as_a_percentage_and_memory_in_bytes()
    {
        var engine = new FakeEngine();
        var container = (await engine.ListContainersAsync()).First(c => c.State == ContainerState.Running);

        using var detail = new ContainerDetailViewModel(engine, container, Font());

        Assert.Collection(detail.Usage.Charts,
            cpu => Assert.Equal(UsageChartUnit.Percent, cpu.Unit),
            mem => Assert.Equal(UsageChartUnit.Bytes, mem.Unit));

        // A container engine has nothing that remembers, so the long ranges must stay shut.
        Assert.False(detail.Usage.HasHistory);
        Assert.All(
            detail.Usage.RangeOptions.Where(o => !UsageGraphs.IsLive(o.Minutes)),
            o => Assert.False(o.IsAvailable, $"{o.Label} was offered without a history source"));

        var usage = await FilledAsync(detail.Usage);
        Assert.NotEmpty(usage.Charts[0].Samples);
        Assert.EndsWith("%", usage.Charts[0].NowText, StringComparison.Ordinal);
    }

    // ── Node ─────────────────────────────────────────────────────────────────

    private static async Task<Node> FirstNodeAsync(FakeClusterEngine cluster) =>
        (await cluster.ListNodesAsync()).First();

    [Fact]
    public async Task A_node_charts_disk_alongside_cpu_and_memory()
    {
        // Disk is the one measure only a node has, and only the kubelet reports it.
        var cluster = new FakeClusterEngine();
        using var detail = new ClusterNodeDetailViewModel(cluster, await FirstNodeAsync(cluster), "v1.29.4");

        Assert.NotNull(detail.Usage);
        Assert.Equal(["CPU", "Memory", "Disk"], detail.Usage!.Charts.Select(c => c.Title));

        var usage = await FilledAsync(detail.Usage);
        Assert.NotEmpty(usage.Charts[2].Samples);
    }

    [Fact]
    public async Task A_node_gets_history_for_cpu_and_memory_but_not_for_disk()
    {
        // node-exporter answers for CPU and memory once joined on node_uname_info. Disk has no
        // stored series — only the kubelet reports it — so that chart stays on the live buffer and
        // keeps its own axis label rather than borrowing the others' range.
        var cluster = new FakeClusterEngine { HasHistory = true };
        using var detail = new ClusterNodeDetailViewModel(cluster, await FirstNodeAsync(cluster), "v1.29.4");

        for (var i = 0; i < 400 && !detail.Usage!.HasHistory; i++)
            await Task.Delay(5);

        Assert.True(detail.Usage!.HasHistory);
        Assert.All(detail.Usage.RangeOptions, o => Assert.True(o.IsAvailable, $"{o.Label} stayed disabled"));

        var disk = detail.Usage.Charts[2];
        Assert.Equal("Disk", disk.Title);

        detail.Usage.SelectRangeCommand.Execute(1440);
        for (var i = 0; i < 400 && detail.Usage.Charts[0].Samples.Count <= 3; i++)
            await Task.Delay(5);

        Assert.True(detail.Usage.Charts[0].Samples.Count > 3, "CPU did not reach the history source");
        Assert.NotEqual("1d", disk.RangeLabel);
    }

    [Fact]
    public async Task A_node_says_that_its_history_measures_memory_differently()
    {
        // node-exporter counts total minus available; the kubelet reports a working set. They sit
        // about a tenth apart, and an unexplained offset between the chart and the figure above it
        // is exactly the kind of quiet wrongness this page keeps producing.
        var cluster = new FakeClusterEngine { HasHistory = true };
        using var detail = new ClusterNodeDetailViewModel(cluster, await FirstNodeAsync(cluster), "v1.29.4");

        for (var i = 0; i < 400 && !detail.Usage!.HasCaveat; i++)
            await Task.Delay(5);

        Assert.True(detail.Usage!.HasCaveat, "the node page drew from history without explaining the offset");
        Assert.Contains("node-exporter", detail.Usage.HistoryCaveat!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pod_page_has_no_caveat_because_its_two_sources_agree()
    {
        // Same cAdvisor series behind both, so there is nothing to warn about — and a note that
        // appeared everywhere would stop being read where it matters.
        var cluster = new FakeClusterEngine { HasHistory = true };
        var pod = (await cluster.ListPodsAsync()).First();

        using var detail = new ClusterPodDetailViewModel(cluster, pod, Font());

        for (var i = 0; i < 400 && !detail.Usage.HasHistory; i++)
            await Task.Delay(5);

        Assert.False(detail.Usage.HasCaveat);
    }

    // ── Workload ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_workload_sums_its_own_pods_and_unlocks_history()
    {
        var cluster = new FakeClusterEngine { HasHistory = true };
        var workload = (await cluster.ListWorkloadsAsync()).First(w => w.Kind == WorkloadKind.Deployment);

        using var detail = new ClusterWorkloadDetailViewModel(cluster, workload);

        for (var i = 0; i < 400 && !detail.Usage!.HasHistory; i++)
            await Task.Delay(5);

        Assert.True(detail.Usage!.HasHistory, "a workload should reach the history source");
        Assert.All(detail.Usage.RangeOptions, o => Assert.True(o.IsAvailable, $"{o.Label} stayed disabled"));
    }

    // ── Namespace ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_namespace_sums_everything_in_it()
    {
        var cluster = new FakeClusterEngine();

        // Whichever namespace the fake actually runs pods in — hard-coding "default" made this
        // assert an empty sum and pass for the wrong reason.
        var pods = await ((IMetricsAware)cluster).Metrics.GetPodUsageAsync();
        Assert.NotEmpty(pods);
        var name = pods[0].Namespace;

        var ns = (await cluster.ListNamespacesAsync()).First(n => n.Name == name);
        using var detail = new ClusterNamespaceDetailViewModel(cluster, ns);

        Assert.NotNull(detail.Usage);
        var usage = await FilledAsync(detail.Usage!);

        pods = [.. pods.Where(p => p.Namespace == name)];
        Assert.True(
            usage.Charts[0].Samples[^1].Value >= pods.Max(p => (double)p.CpuMillicores),
            "the namespace total came in under its largest pod");
    }

    [Fact]
    public void A_cluster_without_metrics_gives_none_of_them_charts()
    {
        var cluster = new FakeClusterEngine(metrics: false);

        using var node = new ClusterNodeDetailViewModel(
            cluster, cluster.ListNodesAsync().GetAwaiter().GetResult().First(), "v1.29.4");

        Assert.Null(node.Usage);
        Assert.False(node.ShowUsageGraphs);
    }
}
