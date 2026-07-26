namespace Kontena.Core.Tooling;

/// <summary>What running a tool to completion produced: exit code plus both streams.</summary>
public readonly record struct ToolResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Ok => ExitCode == 0;

    /// <summary>
    /// Whatever the tool said about failing — stderr, or stdout when stderr is empty. Reporting a
    /// failure in the tool's own words beats "exit code 1" every time.
    /// </summary>
    public string Complaint => StandardError.Length > 0 ? StandardError.Trim() : StandardOutput.Trim();
}
