namespace Kontena.Core.Orchestration.Preflight;

/// <summary>
/// Runs one command on one machine and says what came back. The whole of what a check needs, so a
/// check can be written and tested without a machine anywhere near it.
/// </summary>
public interface IPreflightProbe
{
    /// <summary>What this probe is about — the host's address, and what findings are labelled with.</summary>
    string Target { get; }

    /// <summary>
    /// Runs <paramref name="command"/> and returns what happened. Never throws for a machine that
    /// cannot be reached or a command that exited non-zero: both are answers, and a check that has to
    /// catch exceptions to read them will get one of them wrong.
    /// </summary>
    ValueTask<ProbeResult> RunAsync(string command, CancellationToken ct = default);
}

/// <summary>
/// What running a command produced — or why it produced nothing.
/// </summary>
/// <param name="ExitCode">The command's exit code, or null when it never ran.</param>
/// <param name="Output">Standard output, trimmed. Empty when there was none.</param>
/// <param name="Failure">Why it could not be run, or null when it ran.</param>
public readonly record struct ProbeResult(int? ExitCode, string Output, string? Failure)
{
    /// <summary>Whether the command ran at all. False is <c>PreflightOutcome.Unknown</c> territory.</summary>
    public bool Ran => ExitCode is not null;

    /// <summary>Whether it ran and said yes.</summary>
    public bool Ok => ExitCode == 0;

    public static ProbeResult Success(string output = "") => new(0, output.Trim(), null);

    public static ProbeResult Exit(int code, string output = "") => new(code, output.Trim(), null);

    /// <summary>The command never ran — no connection, no shell, a timeout.</summary>
    public static ProbeResult Unreachable(string why) => new(null, string.Empty, why);
}
