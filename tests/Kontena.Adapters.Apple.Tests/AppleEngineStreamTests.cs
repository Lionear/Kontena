using Kontena.Sdk.Errors;
using Kontena.Sdk.Models;
using Kontena.Sdk.Tooling;
using Kontena.Sdk.Tooling.Fakes;

namespace Kontena.Adapters.Apple.Tests;

/// <summary>
/// Logs, stats and the one-shot exec. The behaviour asserted here was measured against a real
/// <c>container</c> 1.2.2 first — that its logs come out on one channel, that its stats print once and
/// exit, and that its exec hands back the process's own exit code.
/// </summary>
public sealed class AppleEngineStreamTests
{
    private static FakeToolRunner Installed() => new FakeToolRunner().Install(AppleTool.Definition);

    private static AppleEngine Engine(IToolRunner runner) =>
        new(new AppleCli(runner), "apple", "Apple container");

    // ── Logs ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StreamLogsAsync_yields_a_line_at_a_time()
    {
        var runner = Installed().When(_ => true, output: ["first", "second", "third"]);

        var entries = await Engine(runner).StreamLogsAsync("web", follow: false).ToListAsync();

        Assert.Equal(["first", "second", "third"], entries.Select(e => e.Message));
    }

    /// <summary>
    /// <c>container logs</c> writes the container's stderr to its own stdout, so there is nothing to
    /// split. Reporting some lines as stderr would colour them on no evidence — this asserts the
    /// honest answer, including for a line the fake delivers on the error channel.
    /// </summary>
    [Fact]
    public async Task StreamLogsAsync_reports_one_channel_because_the_cli_merges_them()
    {
        var runner = Installed().When(_ => true, output: ["out"], errorOutput: ["err"]);

        var entries = await Engine(runner).StreamLogsAsync("web", follow: false).ToListAsync();

        Assert.All(entries, e => Assert.Equal(LogSource.Stdout, e.Source));
    }

    /// <summary>Following is what keeps the stream open; asking without it is a one-shot read.</summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task StreamLogsAsync_follows_only_when_asked(bool follow, bool expectsFollowFlag)
    {
        var runner = Installed().When(_ => true, output: ["x"]);

        await Engine(runner).StreamLogsAsync("web", follow).ToListAsync();

        Assert.Equal(expectsFollowFlag, Assert.Single(runner.Invocations).Arguments.Contains("--follow"));
    }

    /// <summary>
    /// There is no <c>--timestamps</c> flag, so an entry is stamped with when it was read — unless the
    /// container printed a stamp itself, which is the one case where the time is really the line's.
    /// </summary>
    [Fact]
    public async Task StreamLogsAsync_takes_a_timestamp_the_container_printed_itself()
    {
        var runner = Installed().When(_ => true, output: ["2026-08-09T12:00:00Z hello"]);

        var entry = Assert.Single(await Engine(runner).StreamLogsAsync("web", follow: false).ToListAsync());

        Assert.Equal("hello", entry.Message);
        Assert.Equal(2026, entry.Timestamp.Year);
        Assert.Equal(12, entry.Timestamp.Hour);
    }

    // ── Stats ───────────────────────────────────────────────────────────────

    private const string StatsSample =
        """[{"blockReadBytes":3706880,"blockWriteBytes":0,"cpuUsageUsec":16040,"id":"web","memoryLimitBytes":1073741824,"memoryUsageBytes":4792320,"networkRxBytes":24538,"networkTxBytes":602,"numProcesses":2}]""";

    [Fact]
    public async Task StreamStatsAsync_maps_the_byte_counters_straight_through()
    {
        var runner = Installed().When(_ => true, output: [StatsSample]);

        var sample = await Engine(runner).StreamStatsAsync("web").FirstAsync();

        Assert.Equal("web", sample.ContainerId);
        Assert.Equal(4792320, sample.MemoryUsedBytes);
        Assert.Equal(1073741824, sample.MemoryLimitBytes);
        Assert.Equal(24538, sample.NetRxBytes);
        Assert.Equal(3706880, sample.BlockReadBytes);
    }

    /// <summary>
    /// The CPU figure is a rise between two samples, so the first one has nothing to subtract. Zero is
    /// the honest answer; a number invented from the cumulative counter would put a spike on every graph
    /// the moment it opens.
    /// </summary>
    [Fact]
    public async Task StreamStatsAsync_reports_no_cpu_on_the_first_sample()
    {
        var runner = Installed().When(_ => true, output: [StatsSample]);

        var sample = await Engine(runner).StreamStatsAsync("web").FirstAsync();

        Assert.Equal(0, sample.CpuPercent);
    }

    /// <summary>A container that stopped between samples prints nothing at all — an ending, not a gap.</summary>
    [Fact]
    public async Task StreamStatsAsync_ends_when_the_container_is_gone()
    {
        var runner = Installed().When(_ => true, output: ["[]"]);

        Assert.Empty(await Engine(runner).StreamStatsAsync("web").ToListAsync());
    }

    /// <summary>
    /// The snapshot flag is what makes this poll: without it the CLI is documented to update
    /// continuously, and a caller waiting for the process to end would wait forever.
    /// </summary>
    [Fact]
    public async Task StreamStatsAsync_asks_for_a_snapshot()
    {
        var runner = Installed().When(_ => true, output: [StatsSample]);

        await Engine(runner).StreamStatsAsync("web").FirstAsync();

        var arguments = Assert.Single(runner.Invocations).Arguments;
        Assert.Contains("--no-stream", arguments);
        Assert.Contains("--format", arguments);
    }

    // ── Exec ────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>container exec</c> exits with the code of the process it ran, so a non-zero exit is the
    /// answer rather than a failure — verified against the real CLI with <c>sh -c 'exit 3'</c>.
    /// </summary>
    [Fact]
    public async Task ExecAsync_hands_back_the_exit_code_of_the_command()
    {
        var runner = Installed().When(_ => true, exitCode: 3);

        Assert.Equal(3, await Engine(runner).ExecAsync(
            "web", new ExecRequest { Command = ["sh", "-c", "exit 3"] }));
    }

    /// <summary>
    /// A refusal exits 1 as well, and must not be reported as "your command returned 1". The two
    /// complaints below are verbatim from the real CLI.
    /// </summary>
    [Theory]
    [InlineData("Error: container web is not running")]
    [InlineData("Error: failed to start process abc in container web (cause: \"failed to find target executable nosuchbinary\")")]
    public async Task ExecAsync_raises_a_refusal_instead_of_returning_it_as_an_exit_code(string complaint)
    {
        var runner = Installed().When(_ => true, exitCode: 1, errorOutput: [complaint]);

        await Assert.ThrowsAnyAsync<EngineException>(async () => await Engine(runner).ExecAsync(
            "web", new ExecRequest { Command = ["sh", "-c", "true"] }));
    }

    /// <summary>
    /// A one-shot exec must not ask for a terminal: it would line-buffer the output through a PTY and
    /// fold stderr into stdout, for a caller that only wants an exit code.
    /// </summary>
    [Fact]
    public async Task ExecAsync_does_not_allocate_a_terminal()
    {
        var runner = Installed();

        await Engine(runner).ExecAsync("web", new ExecRequest { Command = ["ls"], Tty = true });

        var arguments = Assert.Single(runner.Invocations).Arguments;
        Assert.DoesNotContain("--tty", arguments);
        Assert.DoesNotContain("--interactive", arguments);
        Assert.Equal(["exec", "web", "ls"], arguments);
    }

    [Fact]
    public async Task ExecAsync_passes_the_working_directory_when_there_is_one()
    {
        var runner = Installed();

        await Engine(runner).ExecAsync(
            "web", new ExecRequest { Command = ["pwd"], WorkingDirectory = "/srv" });

        Assert.Equal(["exec", "--workdir", "/srv", "web", "pwd"], Assert.Single(runner.Invocations).Arguments);
    }

    /// <summary>Opening a terminal needs the binary itself, so a machine without it must say so rather
    /// than spawn a pseudo-terminal around a name that resolves to nothing.</summary>
    [Fact]
    public async Task StartExecSessionAsync_reports_a_missing_binary()
    {
        var runner = new FakeToolRunner();

        await Assert.ThrowsAsync<ToolNotFoundException>(async () => await Engine(runner)
            .StartExecSessionAsync("web", new ExecRequest { Command = ["sh"], Tty = true }));
    }
}
