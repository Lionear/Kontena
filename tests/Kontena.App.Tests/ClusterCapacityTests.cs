using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// The two capacity tiles and the node table's memory column (KON-378). Both read
/// <see cref="Node.Capacity"/> — <c>status.allocatable</c> — so the only things worth pinning are
/// which nodes are counted and what the numbers turn into on screen.
/// </summary>
public sealed class ClusterCapacityTests
{
    private static Node NodeAt(string name, string status, long millicores, long bytes) => new()
    {
        Name = name,
        Status = status,
        Capacity = new NodeCapacity { CpuMillicores = millicores, MemoryBytes = bytes },
    };

    /// <summary>
    /// The one that matters: a node the scheduler cannot reach still reports the capacity it had.
    /// Counting it would leave the tiles at their most reassuring exactly while the cluster is losing
    /// nodes.
    /// </summary>
    [Fact]
    public void Capacity_counts_the_ready_nodes_only()
    {
        var (cpus, memory) = ClusterOverviewViewModel.MaxCapacity(
        [
            NodeAt("a", "Ready", 4000, 8L * 1024 * 1024 * 1024),
            NodeAt("b", "Ready", 4000, 8L * 1024 * 1024 * 1024),
            NodeAt("c", "NotReady", 4000, 8L * 1024 * 1024 * 1024),
            NodeAt("d", "Unknown", 4000, 8L * 1024 * 1024 * 1024),
        ]);

        Assert.Equal("8", cpus);
        Assert.Equal(Format.Size(16L * 1024 * 1024 * 1024), memory);
    }

    /// <summary>
    /// Allocatable is rarely a round core — a GKE 4-core node hands out 3920m — so the fleet total has
    /// to keep its decimal instead of rounding down to a whole CPU that does not exist.
    /// </summary>
    [Fact]
    public void A_fleet_that_does_not_add_up_to_whole_cores_keeps_its_decimal()
    {
        var (cpus, _) = ClusterOverviewViewModel.MaxCapacity(
            [NodeAt("a", "Ready", 3920, 0), NodeAt("b", "Ready", 3920, 0)]);

        Assert.Equal("7.8", cpus);
    }

    [Fact]
    public void A_cluster_with_no_ready_nodes_reports_nothing_available()
    {
        var (cpus, memory) = ClusterOverviewViewModel.MaxCapacity([NodeAt("a", "NotReady", 4000, 4096)]);

        Assert.Equal("0", cpus);
        Assert.Equal(Format.Size(0), memory);
    }

    /// <summary>
    /// The table's memory column in the same "used / capacity" shape the CPU column already uses, and
    /// the same shape the Nodes page's cards use — three places reading one source should not read
    /// three different ways.
    /// </summary>
    [Fact]
    public async Task Every_node_row_states_its_memory_the_way_it_states_its_cpu()
    {
        using var page = new ClusterOverviewViewModel(new FakeClusterEngine());

        await page.LoadAsync();

        var row = Assert.Single(page.Nodes, n => n.Name == "gke-prod-worker-1");
        Assert.Equal("1200m / 4000m", row.Cpu);
        Assert.Equal(
            $"{Format.Size(6L * 1024 * 1024 * 1024)} / {Format.Size(16L * 1024 * 1024 * 1024)}",
            row.Memory);
    }
}
