using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Kontena.Core.Tooling;

/// <summary>
/// Runs external tools out of process. These are CLIs by nature — there is no library form worth
/// binding to — so the invocation lives in one place: no shell, an argument list rather than a
/// command string (nothing to quote, nothing to inject), and both streams captured so a failure can
/// be reported in the tool's own words.
/// </summary>
public sealed class ToolRunner : IToolRunner
{
    /// <summary>A quiet, buffered command that has not finished by now is stuck, not slow.</summary>
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Asking a tool its version should be instant; anything else means something is wrong.</summary>
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(10);

    public async ValueTask<ToolLocation> FindAsync(ExternalTool tool, CancellationToken ct = default)
    {
        var path = ToolLocator.Locate(tool.Executable, tool.ExtraSearchPaths);
        if (path is null)
            return ToolLocation.Missing(tool);

        // Present is not the same as usable: a Homebrew shim left behind by an uninstall, a wrapper
        // pointing at a deleted binary, or the wrong architecture all sit on disk and refuse to run.
        // Asking the version is the cheapest way to tell those apart, and the answer is worth having.
        try
        {
            var result = await ExecuteAsync(
                path, tool.VersionArguments, workingDirectory: null, environment: null,
                timeout: VersionTimeout, ct);

            return new ToolLocation(tool, path, result.Ok ? FirstLine(result.StandardOutput, result.StandardError) : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ToolLocation(tool, path, null);
        }
    }

    public async ValueTask<ToolResult> RunAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        var path = Require(invocation.Tool);
        return await ExecuteAsync(
            path, invocation.Arguments, invocation.WorkingDirectory, invocation.Environment,
            invocation.Timeout ?? DefaultTimeout, ct);
    }

    public async IAsyncEnumerable<ToolLine> StreamAsync(
        ToolInvocation invocation, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var path = Require(invocation.Tool);

        using var process = Start(
            path, invocation.Arguments, invocation.WorkingDirectory, invocation.Environment,
            redirectForStreaming: true);

        // One channel fed by both readers, so lines keep the order they arrived in. Splitting stdout
        // and stderr into two sequences would shuffle a tool's own narration out of sequence — and
        // kind, minikube and kubectl all narrate progress on stderr.
        var lines = Channel.CreateUnbounded<ToolLine>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        using var timeout = Linked(invocation.Timeout, ct);
        var token = timeout.Token;

        var pump = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(
                    PumpAsync(process.StandardOutput, ToolOutputKind.Out, lines.Writer, token),
                    PumpAsync(process.StandardError, ToolOutputKind.Error, lines.Writer, token));

                await process.WaitForExitAsync(token);
                lines.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                lines.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        var tail = new Tail();
        try
        {
            await foreach (var line in lines.Reader.ReadAllAsync(CancellationToken.None))
            {
                tail.Remember(line);
                yield return line;
            }
        }
        finally
        {
            // Whatever ended the enumeration — completion, cancellation, or the caller walking away
            // mid-stream — the child must not outlive it. A `kind create` left running would keep
            // building a cluster nobody is watching.
            Kill(process);
            await Settle(pump);
        }

        if (process.ExitCode != 0)
            throw new ToolFailedException(invocation.CommandLine, process.ExitCode, tail.Complaint);
    }

    private static string Require(ExternalTool tool)
        => ToolLocator.Locate(tool.Executable, tool.ExtraSearchPaths)
           ?? throw new ToolNotFoundException(tool.Name);

    private static async Task<ToolResult> ExecuteAsync(
        string path,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string?>? environment,
        TimeSpan? timeout,
        CancellationToken ct)
    {
        using var process = Start(path, arguments, workingDirectory, environment, redirectForStreaming: false);
        using var linked = Linked(timeout, ct);

        // Read both streams concurrently: a tool that fills one pipe while we drain the other would
        // otherwise deadlock the moment its output outgrows the buffer.
        var stdout = process.StandardOutput.ReadToEndAsync(linked.Token);
        var stderr = process.StandardError.ReadToEndAsync(linked.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token);
            return new ToolResult(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
    }

    private static Process Start(
        string path,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string?>? environment,
        bool redirectForStreaming)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (!string.IsNullOrEmpty(workingDirectory))
            startInfo.WorkingDirectory = workingDirectory;

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        foreach (var (key, value) in environment ?? new Dictionary<string, string?>())
        {
            if (value is null)
                startInfo.Environment.Remove(key);
            else
                startInfo.Environment[key] = value;
        }

        if (redirectForStreaming)
        {
            // Tools that detect a pipe often switch to a terse, buffered mode. Saying "no colour"
            // explicitly keeps escape sequences out of the console without asking for a pty.
            startInfo.Environment["NO_COLOR"] = "1";
            startInfo.Environment["TERM"] = "dumb";
        }

        var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            process.Dispose();
            throw new ToolNotFoundException(Path.GetFileNameWithoutExtension(path));
        }

        return process;
    }

    private static async Task PumpAsync(
        StreamReader reader, ToolOutputKind stream, ChannelWriter<ToolLine> writer, CancellationToken ct)
    {
        while (await reader.ReadLineAsync(ct) is { } text)
            await writer.WriteAsync(new ToolLine(stream, text), ct);
    }

    private static CancellationTokenSource Linked(TimeSpan? timeout, CancellationToken ct)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } window)
            source.CancelAfter(window);

        return source;
    }

    private static async Task Settle(Task pump)
    {
        try
        {
            await pump;
        }
        catch
        {
            // The pump's failure is either the exception the caller is already unwinding with, or a
            // read that lost its race with the kill above. Neither adds anything here.
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Exited between the check and the kill — nothing to clean up.
        }
    }

    private static string? FirstLine(string stdout, string stderr)
    {
        // Some tools print their version to stderr (kubectl used to). Take whichever spoke.
        var text = stdout.Trim().Length > 0 ? stdout : stderr;
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return string.IsNullOrEmpty(line) ? null : line;
    }

    /// <summary>
    /// Keeps the last few lines so a failure can be explained. Only the tail: a build or a cluster
    /// create emits thousands of lines, and the useful ones are at the end.
    /// </summary>
    private sealed class Tail
    {
        private const int Keep = 10;
        private readonly Queue<string> _lines = new();

        public void Remember(ToolLine line)
        {
            if (string.IsNullOrWhiteSpace(line.Text))
                return;

            _lines.Enqueue(line.Text.Trim());
            if (_lines.Count > Keep)
                _lines.Dequeue();
        }

        public string Complaint => string.Join(Environment.NewLine, _lines);
    }
}
