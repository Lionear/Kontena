namespace Kontena.Sdk.Tooling;

/// <summary>
/// The outcome of looking for a tool. Absent and present-but-unreadable are different states on
/// purpose: "install it" is the wrong advice for a binary that is there but will not run.
/// </summary>
/// <param name="Tool">What was looked for.</param>
/// <param name="Path">Absolute path to the executable, or null when it was not found.</param>
/// <param name="Version">Whatever the tool answered when asked, trimmed to one line. Null when it
/// was not found, or when it could not be asked.</param>
public sealed record ToolLocation(ExternalTool Tool, string? Path, string? Version)
{
    /// <summary>True when the tool is on this machine and answered.</summary>
    public bool Found => Path is not null;

    /// <summary>
    /// Found, but it would not say what version it is — usually a broken install, a wrapper script
    /// pointing at nothing, or a binary for the wrong architecture. Worth separating from "missing",
    /// because installing it again is not the fix.
    /// </summary>
    public bool FoundButUnusable => Path is not null && Version is null;

    public static ToolLocation Missing(ExternalTool tool) => new(tool, null, null);
}
