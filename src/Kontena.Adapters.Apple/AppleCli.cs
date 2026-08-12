using System.Text.Json;
using Kontena.Sdk.Errors;
using Kontena.Sdk.Tooling;

namespace Kontena.Adapters.Apple;

/// <summary>
/// The one place that shells out to <c>container</c>, and the one place that decides what a non-zero
/// exit means. Nothing else in this adapter builds a <see cref="ToolInvocation"/> or touches
/// <see cref="IToolRunner"/> directly.
/// <para>
/// Centralising the error mapping is the point. <c>container</c> reports a missing resource in two
/// different shapes — <c>Error: container not found: web</c> from <c>inspect</c>, and
/// <c>Error: internalError: "failed to stop container" (cause: "notFound: …")</c> from <c>stop</c> —
/// and both exit 1, exactly like every other failure. Reading that distinction once here is what lets
/// the rest of the adapter throw the typed <see cref="ResourceNotFoundException"/> the UI already knows
/// how to show, instead of each call site re-deciding what "exit 1" meant.
/// </para>
/// </summary>
internal sealed class AppleCli(IToolRunner runner)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Runs a short <c>container</c> command to completion and returns its stdout — for listing and
    /// inspecting, not for logs. Use <see cref="StreamAsync"/> for anything a user watches progress on.
    /// </summary>
    /// <exception cref="ToolNotFoundException">
    /// <c>container</c> is not installed. Passed through unchanged: the engine layer decides how a
    /// missing binary is reported, not this one.
    /// </exception>
    /// <exception cref="ResourceNotFoundException">The command named something that does not exist.</exception>
    /// <exception cref="EngineException">Anything else the CLI refused to do.</exception>
    public async ValueTask<string> RunAsync(CancellationToken ct, params string[] args)
    {
        var invocation = new ToolInvocation(AppleTool.Definition, args);
        var result = await runner.RunAsync(invocation, ct).ConfigureAwait(false);

        if (!result.Ok)
            throw Failure(invocation.CommandLine, result.Complaint);

        return result.StandardOutput;
    }

    /// <summary>
    /// Runs a <c>container</c> command and yields its output as it arrives — for <c>logs</c> and
    /// anything else slow enough that buffered output would be indistinguishable from a hang.
    /// </summary>
    public IAsyncEnumerable<ToolLine> StreamAsync(CancellationToken ct, params string[] args) =>
        runner.StreamAsync(new ToolInvocation(AppleTool.Definition, args), ct);

    /// <summary>
    /// Runs a command whose non-zero exit is an <b>answer</b> rather than a failure, and hands the code
    /// back instead of throwing. Only <c>exec</c> is like this: <c>container exec</c> exits with the
    /// exit code of the process it ran, so <c>sh -c 'exit 3'</c> gives 3.
    /// <para>
    /// That leaves exit 1 ambiguous — it is what a command that returned 1 gives, and also what a
    /// refusal gives. The complaint text is what separates them, so a refusal is still raised: a
    /// container that is not running, or a binary that is not in the image, must not come back as "your
    /// command exited 1".
    /// </para>
    /// </summary>
    public async ValueTask<int> RunForExitCodeAsync(CancellationToken ct, params string[] args)
    {
        var invocation = new ToolInvocation(AppleTool.Definition, args);
        var result = await runner.RunAsync(invocation, ct).ConfigureAwait(false);

        if (!result.Ok && Refused(result.Complaint))
            throw Failure(invocation.CommandLine, result.Complaint);

        return result.ExitCode;
    }

    /// <summary>
    /// Whether a complaint is the CLI refusing to run the command at all, rather than the command
    /// itself having failed. Both observed refusals name what went wrong before the process started —
    /// "container X is not running", and a nested "failed to start process" for a binary the image does
    /// not have.
    /// </summary>
    private static bool Refused(string complaint) =>
        complaint.Contains("is not running", StringComparison.OrdinalIgnoreCase) ||
        complaint.Contains("failed to start process", StringComparison.OrdinalIgnoreCase) ||
        complaint.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
        complaint.Contains("notFound", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Where the <c>container</c> binary is. The pseudo-terminal spawns it directly rather than through
    /// <see cref="IToolRunner"/> — that seam reads output and cannot type — so the terminal needs the
    /// path the runner would have used, not a bare name resolved against whatever PATH the app inherited.
    /// </summary>
    /// <exception cref="ToolNotFoundException">The binary is not on this machine.</exception>
    public async ValueTask<string> LocateAsync(CancellationToken ct)
    {
        var location = await runner.FindAsync(AppleTool.Definition, ct).ConfigureAwait(false);

        return location.Path ?? throw new ToolNotFoundException(AppleTool.Definition.Name);
    }

    /// <summary>
    /// Runs a command whose output is a JSON array and deserializes it. Every <c>--format json</c>
    /// command in this CLI prints a genuine array — not the NDJSON nerdctl prints — so one parser
    /// covers all of them.
    /// </summary>
    /// <exception cref="EngineException">
    /// The CLI printed something this adapter cannot read. A caller that got an empty list because the
    /// output was malformed would show "no containers" about a machine that has them, so this fails
    /// loudly rather than degrading to nothing.
    /// </exception>
    public async ValueTask<IReadOnlyList<T>> ListAsync<T>(CancellationToken ct, params string[] args)
    {
        var stdout = await RunAsync(ct, args).ConfigureAwait(false);
        return Parse<T>(stdout, ToolCommand.Describe(AppleTool.Definition.Executable, args));
    }

    /// <summary>
    /// Runs a command whose output is a single JSON object rather than an array. Only
    /// <c>system df</c> is shaped that way — everything else this adapter reads is a list.
    /// </summary>
    public async ValueTask<T?> GetAsync<T>(CancellationToken ct, params string[] args)
    {
        var stdout = await RunAsync(ct, args).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(stdout))
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(stdout, JsonOptions);
        }
        catch (JsonException error)
        {
            var commandLine = ToolCommand.Describe(AppleTool.Definition.Executable, args);
            throw new EngineException($"Could not read the output of `{commandLine}`.", error);
        }
    }

    /// <summary>Deserializes one of the CLI's JSON arrays. Internal so the tests can reach it with
    /// captured output without going through a process.</summary>
    internal static IReadOnlyList<T> Parse<T>(string stdout, string commandLine)
    {
        // An empty stdout is a real answer from this CLI in at least one case: `system df` on a fresh
        // install. Treating it as "nothing" beats failing, and every other command prints "[]".
        if (string.IsNullOrWhiteSpace(stdout))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<T>>(stdout, JsonOptions) ?? [];
        }
        catch (JsonException error)
        {
            throw new EngineException($"Could not read the output of `{commandLine}`.", error);
        }
    }

    /// <summary>
    /// Turns a non-zero exit into the right exception. The exit code carries nothing — it is 1 for a
    /// missing container and 1 for a stopped apiserver alike — so the complaint text is the only signal
    /// there is.
    /// </summary>
    private static EngineException Failure(string commandLine, string complaint)
    {
        // Both shapes contain "not found" once case is ignored ("container not found: web" and the
        // nested `cause: "notFound: …"`), which is why this matches on that rather than on either
        // sentence in full: the wrapper text around the cause is internal wording that can change,
        // while the condition it reports cannot.
        if (complaint.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            complaint.Contains("notFound", StringComparison.OrdinalIgnoreCase))
        {
            return new ResourceNotFoundException(complaint);
        }

        // The apiserver is a launchd service that `container system start` brings up, so "it is
        // installed but nothing is listening" is an ordinary state here, not a broken install — and it
        // is the one the switcher must report as unreachable rather than as a failed command.
        if (complaint.Contains("failed to connect", StringComparison.OrdinalIgnoreCase) ||
            complaint.Contains("apiserver", StringComparison.OrdinalIgnoreCase) ||
            complaint.Contains("XPC", StringComparison.Ordinal))
        {
            return new EngineUnreachableException(complaint);
        }

        return new EngineException($"`{commandLine}` failed: {complaint}");
    }
}
