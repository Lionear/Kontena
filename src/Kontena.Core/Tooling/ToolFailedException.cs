namespace Kontena.Core.Tooling;

/// <summary>
/// Raised when a tool ran and exited non-zero. Carries the command and the tool's own last words,
/// because "kind exited with 1" tells a user nothing they can act on.
/// </summary>
public sealed class ToolFailedException(string commandLine, int exitCode, string complaint)
    : Exception(Compose(commandLine, exitCode, complaint))
{
    public string CommandLine { get; } = commandLine;
    public int ExitCode { get; } = exitCode;

    /// <summary>What the tool said as it failed — its stderr, or stdout when stderr was empty.</summary>
    public string Complaint { get; } = complaint;

    private static string Compose(string commandLine, int exitCode, string complaint)
        => string.IsNullOrWhiteSpace(complaint)
            ? $"{commandLine} exited with {exitCode}."
            : $"{commandLine} exited with {exitCode}: {complaint}";
}
