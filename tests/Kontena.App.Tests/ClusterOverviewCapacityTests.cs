using Kontena.App.ViewModels;
using Kontena.Core.Models;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// The overview's two capacity tiles and the node table's memory column (KON-378). Both read the
/// node's own allocatable figures rather than the metrics source, which is what lets them answer on a
/// cluster with no metrics-server — the fake seeds three Ready nodes of 4000m and 16 GiB each.
/// </summary>
public sealed class ClusterOverviewCapacityTests
{
    private static Node At(string name, string status, long millicores, long gib) => new()
    {
        Name = name,
        Status = status,
        Capacity = new NodeCapacity { CpuMillicores = millicores, MemoryBytes = gib * 1024 * 1024 * 1024 },
    };

    [Fact]
    public void The_tiles_add_up_the_allocatable_capacity()
    {
        var (cpu, memory) = ClusterOverviewViewModel.Ceiling(
            [At("a", "Ready", 4000, 16), At("b", "Ready", 4000, 16)]);

        Assert.Equal("8", cpu);
        Assert.Equal(ByteSize.Format(32L * 1024 * 1024 * 1024), memory);
    }

    /// <summary>
    /// The reason the rule exists: a node that stopped answering still reports the capacity it had, and
    /// a ceiling that counts it promises cores nothing can be scheduled onto.
    /// </summary>
    [Fact]
    public void A_node_that_is_not_ready_is_not_capacity()
    {
        var (cpu, memory) = ClusterOverviewViewModel.Ceiling(
            [At("a", "Ready", 4000, 16), At("b", "NotReady", 4000, 16), At("c", "Unknown", 4000, 16)]);

        Assert.Equal("4", cpu);
        Assert.Equal(ByteSize.Format(16L * 1024 * 1024 * 1024), memory);
    }

    /// <summary>Cores, not milli-cores, and only where the fraction is worth a decimal.</summary>
    [Theory]
    [InlineData(0, "0")]
    [InlineData(7910, "7.9")]
    [InlineData(31640, "31.6")]
    [InlineData(32000, "32")]
    public void A_total_reads_in_whole_cores(long millicores, string expected) =>
        Assert.Equal(expected, Format.Cores(millicores));

    [Fact]
    public async Task Every_node_row_carries_memory_beside_its_cpu()
    {
        using var page = new ClusterOverviewViewModel(new FakeClusterEngine());
        await page.LoadAsync();

        Assert.Equal("12", page.MaxCpu);
        Assert.Equal(ByteSize.Format(48L * 1024 * 1024 * 1024), page.MaxMemory);

        var node = page.Nodes[0];
        Assert.Equal("1200m / 4000m", node.Cpu);
        Assert.Equal(
            $"{ByteSize.Format(6L * 1024 * 1024 * 1024)} / {ByteSize.Format(16L * 1024 * 1024 * 1024)}",
            node.Memory);
    }
}
