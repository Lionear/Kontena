namespace Kontena.Sdk.Tooling;

/// <summary>
/// One thing to run: which tool, with which arguments, where.
/// </summary>
/// <param name="Tool">The tool. Located at call time, so a run started after an install finds it.</param>
/// <param name="Arguments">Arguments as a list — never a command string, so there is nothing to quote
/// wrong and nothing to inject.</param>
public sealed record ToolInvocation(ExternalTool Tool, IReadOnlyList<string> Arguments)
{
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Extra environment variables for this run. Values here are set on top of the inherited
    /// environment; a null value removes the variable.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; }
        = new Dictionary<string, string?>();

    /// <summary>
    /// How long to allow before treating the run as stuck. Null means "as long as it takes", which is
    /// right for anything streaming: the user is watching output and can cancel, and a cluster that
    /// takes four minutes on a slow connection is not a hang.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>The invocation as a person would type it — for logs, consoles and error messages.</summary>
    public string CommandLine => ToolCommand.Describe(Tool.Executable, Arguments);
}
