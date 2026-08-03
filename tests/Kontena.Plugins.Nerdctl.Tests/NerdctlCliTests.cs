using Kontena.Sdk.Tooling;
using Kontena.Sdk.Tooling.Fakes;

namespace Kontena.Plugins.Nerdctl.Tests;

/// <summary>
/// The one property this seam exists to guarantee: no invocation can leave without a namespace. Getting
/// it wrong does not fail loudly — it shows an empty list while containers run in a different namespace
/// — so most tests here check the same thing from a different angle. Uses the SDK's own
/// <see cref="FakeToolRunner"/> (Kontena.Sdk.Tooling.Fakes) rather than a new fake: it already records
/// invocations, scripts output and reproduces <see cref="ToolNotFoundException"/> for an uninstalled tool.
/// </summary>
public sealed class NerdctlCliTests
{
    private static NerdctlCli Cli(FakeToolRunner runner) => new(runner, "k8s.io");

    private static FakeToolRunner Installed() => new FakeToolRunner().Install(NerdctlTool.Definition);

    [Fact]
    public async Task The_namespace_comes_before_the_subcommand()
    {
        var runner = Installed();

        await Cli(runner).RunAsync(CancellationToken.None, "ps", "-a");

        Assert.Equal(["--namespace", "k8s.io", "ps", "-a"], runner.Invocations[^1].Arguments);
    }

    [Fact]
    public async Task Subcommand_arguments_keep_their_order_after_the_namespace()
    {
        var runner = Installed();

        await Cli(runner).RunAsync(CancellationToken.None, "images", "--format", "json");

        Assert.Equal(
            ["--namespace", "k8s.io", "images", "--format", "json"],
            runner.Invocations[^1].Arguments);
    }

    [Fact]
    public async Task RunAsync_returns_the_tools_stdout()
    {
        var runner = Installed().When(_ => true, output: ["""{"Name":"k8s.io"}"""]);

        var stdout = await Cli(runner).RunAsync(CancellationToken.None, "namespace", "ls");

        Assert.Equal("""{"Name":"k8s.io"}""", stdout);
    }

    [Fact]
    public async Task RunAsync_throws_on_a_non_zero_exit()
    {
        var runner = Installed().When(_ => true, errorOutput: ["no such container"], exitCode: 1);

        var ex = await Assert.ThrowsAsync<ToolFailedException>(
            () => Cli(runner).RunAsync(CancellationToken.None, "rm", "ghost").AsTask());

        Assert.Equal(1, ex.ExitCode);
    }

    [Fact]
    public async Task StreamAsync_also_gets_the_namespace_before_the_subcommand()
    {
        var runner = Installed().When(_ => true, output: ["line one"]);

        await foreach (var _ in Cli(runner).StreamAsync(CancellationToken.None, "logs", "--tail", "10", "abc"))
        {
        }

        Assert.Equal(
            ["--namespace", "k8s.io", "logs", "--tail", "10", "abc"],
            runner.Invocations[^1].Arguments);
    }

    [Fact]
    public async Task StreamAsync_yields_the_tools_lines_in_order()
    {
        var runner = Installed().When(_ => true, output: ["first", "second"]);

        var lines = new List<string>();
        await foreach (var line in Cli(runner).StreamAsync(CancellationToken.None, "logs", "abc"))
            lines.Add(line.Text);

        Assert.Equal(["first", "second"], lines);
    }

    [Fact]
    public async Task A_missing_tool_surfaces_as_ToolNotFoundException_unchanged_from_RunAsync()
    {
        // The engine decides how a missing binary is reported to the switcher, not this layer — this
        // only pins that the exception passes through untouched, not translated into something else.
        await Assert.ThrowsAsync<ToolNotFoundException>(
            () => Cli(new FakeToolRunner()).RunAsync(CancellationToken.None, "ps").AsTask());
    }

    [Fact]
    public async Task A_missing_tool_surfaces_as_ToolNotFoundException_unchanged_from_StreamAsync()
    {
        await Assert.ThrowsAsync<ToolNotFoundException>(async () =>
        {
            await foreach (var _ in Cli(new FakeToolRunner()).StreamAsync(CancellationToken.None, "logs", "abc"))
            {
            }
        });
    }
}
