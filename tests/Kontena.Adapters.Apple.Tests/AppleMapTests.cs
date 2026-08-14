using Kontena.Sdk.Models;

namespace Kontena.Adapters.Apple.Tests;

/// <summary>
/// The mapping decisions that are not visible in a fixture-driven read test, because the fixtures only
/// contain the one case this machine happened to produce.
/// </summary>
public sealed class AppleMapTests
{
    /// <summary>
    /// A registry port is not a tag. <c>localhost:5000/app</c> splitting on the last colon would file
    /// every image from a private registry under repository "localhost" and tag "5000/app" — wrong, and
    /// wrong in a way that still renders.
    /// </summary>
    [Theory]
    [InlineData("docker.io/library/alpine:3.20", "docker.io/library/alpine", "3.20")]
    [InlineData("localhost:5000/app:v1", "localhost:5000/app", "v1")]
    [InlineData("localhost:5000/app", "localhost:5000/app", "<none>")]
    [InlineData("alpine", "alpine", "<none>")]
    [InlineData("repo@sha256:abc", "repo", "<none>")]
    [InlineData("", "", "<none>")]
    public void SplitReference_only_treats_a_colon_after_the_last_slash_as_a_tag(
        string reference, string repository, string tag)
    {
        Assert.Equal((repository, tag), AppleMap.SplitReference(reference));
    }

    /// <summary>
    /// Only two lifecycle words have been observed. Anything else is reported as unknown rather than
    /// mapped to a neighbouring state: a wrong status dot is a lie, an unknown one is not.
    /// </summary>
    [Theory]
    [InlineData("running", ContainerState.Running)]
    [InlineData("stopped", ContainerState.Exited)]
    [InlineData("created", ContainerState.Created)]
    [InlineData("something-new", ContainerState.Unknown)]
    [InlineData(null, ContainerState.Unknown)]
    public void State_maps_only_what_has_been_seen(string? state, ContainerState expected)
    {
        Assert.Equal(expected, AppleMap.State(state));
    }

    /// <summary>
    /// The CPU percentage is the whole reason stats needs two samples. One core saturated for the full
    /// interval is 100%: a second of CPU time over a second of wall clock. Two seconds of CPU time in
    /// one second is a container on two cores, and reads as 200% — the same scale Docker uses, so the
    /// gauge does not need to know which backend it is showing.
    /// </summary>
    [Theory]
    [InlineData(1_000_000, 1, 100)]
    [InlineData(500_000, 1, 50)]
    [InlineData(2_000_000, 1, 200)]
    [InlineData(4_254, 2, 0.2127)]
    public void Stats_reads_the_cpu_counter_as_a_rise_over_the_interval(
        long consumedUsec, int seconds, double expectedPercent)
    {
        var previous = new AppleStats { Id = "web", CpuUsageUsec = 1_000 };
        var current = previous with { CpuUsageUsec = 1_000 + consumedUsec };

        var stats = AppleMap.Stats(current, previous, TimeSpan.FromSeconds(seconds), "web");

        Assert.Equal(expectedPercent, stats.CpuPercent, precision: 4);
    }

    /// <summary>
    /// A counter that went backwards means the container restarted between samples, so the two are not
    /// comparable. Zero says "no reading"; subtracting anyway would draw a negative percentage.
    /// </summary>
    [Fact]
    public void Stats_reports_no_cpu_when_the_counter_restarted()
    {
        var previous = new AppleStats { Id = "web", CpuUsageUsec = 900_000 };
        var current = previous with { CpuUsageUsec = 12 };

        var stats = AppleMap.Stats(current, previous, TimeSpan.FromSeconds(2), "web");

        Assert.Equal(0, stats.CpuPercent);
    }

    /// <summary>An interval of zero would divide by it.</summary>
    [Fact]
    public void Stats_reports_no_cpu_without_an_interval()
    {
        var previous = new AppleStats { Id = "web", CpuUsageUsec = 1_000 };
        var current = previous with { CpuUsageUsec = 2_000 };

        Assert.Equal(0, AppleMap.Stats(current, previous, TimeSpan.Zero, "web").CpuPercent);
    }

    /// <summary>The apiserver's entry holds a sentence, not a version; picking by name is the point.</summary>
    [Fact]
    public void Version_picks_the_cli_entry()
    {
        var entries = new List<AppleVersion>
        {
            new() { AppName = "container-apiserver", Version = "container-apiserver version 1.2.2 (build: release)" },
            new() { AppName = "container", Version = "1.2.2" },
        };

        Assert.Equal("1.2.2", AppleMap.Version(entries));
    }

    /// <summary>A version that cannot be found is an empty string, not the wrong entry.</summary>
    [Fact]
    public void Version_is_empty_when_the_cli_entry_is_absent()
    {
        var entries = new List<AppleVersion> { new() { AppName = "container-apiserver", Version = "…" } };

        Assert.Equal(string.Empty, AppleMap.Version(entries));
    }
}
