namespace Kontena.Core.Tooling;

/// <summary>
/// How to install a tool with one particular package manager — the exact command, so it can be shown,
/// copied, or (on request) run.
/// </summary>
/// <param name="Manager">The package manager this command belongs to.</param>
/// <param name="Executable">The program to run, e.g. "brew".</param>
/// <param name="Arguments">Its arguments, as a list — never a command string.</param>
/// <param name="RequiresElevation">Whether this needs root/administrator. Worth knowing before the
/// prompt appears rather than after.</param>
public sealed record InstallHint(
    PackageManager Manager,
    string Executable,
    IReadOnlyList<string> Arguments,
    bool RequiresElevation = false)
{
    /// <summary>The command as a person would type it, for display and for copying.</summary>
    public string CommandLine => ToolCommand.Describe(Executable, Arguments);
}
