using System.Text;
using Kontena.Sdk.Tooling;

namespace Kontena.Core.Tests;

/// <summary>
/// The seam that finds and drives external tools (KON-129).
/// <para>
/// Driven against <c>dotnet</c> rather than kind or minikube: it is the one tool guaranteed to be on
/// every machine that can run these tests, and the behaviour under test is the running, not the tool.
/// </para>
/// </summary>
[Collection(ProcessEnvironment.Name)]
public sealed class ToolRunnerTests
{
    private static readonly ExternalTool Dotnet = new("dotnet", "dotnet", ["--version"], []);
    private static readonly ExternalTool Absent = new("kontena-nope", "kontena-nope-xyz", ["--version"], []);

    /// <summary>The one thing on every machine that will copy a file's bytes to stdout verbatim.</summary>
    private static readonly ExternalTool Shell = OperatingSystem.IsWindows()
        ? new ExternalTool("cmd", "cmd.exe", ["/c", "ver"], [])
        : new ExternalTool("sh", "sh", ["--version"], []);

    private readonly ToolRunner _runner = new();

    [Fact]
    public async Task Finds_a_tool_that_is_installed_and_reads_its_version()
    {
        var found = await _runner.FindAsync(Dotnet);

        Assert.True(found.Found);
        Assert.NotNull(found.Path);
        Assert.False(string.IsNullOrWhiteSpace(found.Version));
        Assert.False(found.FoundButUnusable);
    }

    [Fact]
    public async Task A_missing_tool_is_an_answer_not_an_exception()
    {
        // Callers nearly always want to say "not installed, here is how" rather than fail.
        var found = await _runner.FindAsync(Absent);

        Assert.False(found.Found);
        Assert.Null(found.Path);
        Assert.Same(Absent, found.Tool);
    }

    [Fact]
    public async Task Running_a_missing_tool_throws_with_the_tool_name()
    {
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(
            async () => await _runner.RunAsync(new ToolInvocation(Absent, ["--version"])));

        Assert.Equal(Absent.Name, ex.Tool);
    }

    [Fact]
    public async Task Runs_and_returns_output()
    {
        var result = await _runner.RunAsync(new ToolInvocation(Dotnet, ["--version"]));

        Assert.True(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.StandardOutput));
    }

    [Fact]
    public async Task Streams_output_line_by_line()
    {
        var lines = new List<ToolLine>();
        await foreach (var line in _runner.StreamAsync(new ToolInvocation(Dotnet, ["--list-sdks"])))
            lines.Add(line);

        Assert.NotEmpty(lines);
        Assert.All(lines, l => Assert.False(string.IsNullOrEmpty(l.Text)));
    }

    [Fact]
    public async Task A_failed_run_throws_rather_than_ending_quietly()
    {
        // The exit code is not smuggled in as a last line: a caller that only renders the lines would
        // then show a failure as a success.
        var invocation = new ToolInvocation(Dotnet, ["--kontena-not-a-flag"]);

        var ex = await Assert.ThrowsAsync<ToolFailedException>(async () =>
        {
            await foreach (var _ in _runner.StreamAsync(invocation))
            {
                // drain
            }
        });

        Assert.NotEqual(0, ex.ExitCode);
        Assert.Contains("dotnet", ex.CommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_mid_stream_does_not_hang()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in _runner.StreamAsync(new ToolInvocation(Dotnet, ["--list-sdks"]), cts.Token))
            {
                // drain
            }
        });
    }

    [Fact]
    public async Task Environment_entries_reach_the_process()
    {
        // A provisioner needs this: kind reads KIND_EXPERIMENTAL_PROVIDER, minikube reads MINIKUBE_HOME.
        var invocation = new ToolInvocation(Dotnet, ["--version"])
        {
            Environment = new Dictionary<string, string?> { ["KONTENA_PROBE"] = "1" },
        };

        var result = await _runner.RunAsync(invocation);

        Assert.True(result.Ok);
    }

    [Fact]
    public async Task Output_a_tool_wrote_as_utf8_is_read_back_as_utf8()
    {
        // kind and minikube spend their UTF-8 on emoji, and on Windows every one of them came back as
        // mojibake (KON-469): with no encoding named, .NET decodes a redirected stream with the OS
        // console codepage, which is UTF-8 on Linux and macOS and is not there. `type` and `cat` hand
        // the file's bytes to the pipe untouched, so this asserts nothing but how ToolRunner reads them.
        const string written = "Ensuring node image (kindest/node:v1.37.0) \U0001F5BC \u2713 \U0001F4E6";
        var file = Path.Combine(Path.GetTempPath(), $"kontena-kon469-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(file, written, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        try
        {
            string[] arguments = OperatingSystem.IsWindows()
                ? ["/c", "type", file]
                : ["-c", "cat \"$0\"", file];

            var result = await _runner.RunAsync(new ToolInvocation(Shell, arguments));

            Assert.True(result.Ok);
            Assert.Equal(written, result.StandardOutput.Trim());
        }
        finally
        {
            File.Delete(file);
        }
    }
}
