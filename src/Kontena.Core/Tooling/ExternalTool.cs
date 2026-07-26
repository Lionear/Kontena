namespace Kontena.Core.Tooling;

/// <summary>
/// An external command-line tool Kontena drives but does not ship: kind, minikube, kubectl, helm,
/// podman. Describes how to find it, how to ask its version, and how someone would install it.
/// </summary>
/// <param name="Name">How the tool is called in a sentence, e.g. "kind".</param>
/// <param name="Executable">The file to look for, without a platform extension.</param>
/// <param name="VersionArguments">What to pass to make it print its version.</param>
/// <param name="InstallHints">Per package manager, the command that installs it. Shown, never run
/// behind the user's back.</param>
public sealed record ExternalTool(
    string Name,
    string Executable,
    IReadOnlyList<string> VersionArguments,
    IReadOnlyList<InstallHint> InstallHints)
{
    /// <summary>
    /// Directories to search besides PATH, on top of the platform defaults. Rarely needed — the
    /// defaults in <see cref="ToolLocator"/> cover the usual package managers.
    /// </summary>
    public IReadOnlyList<string> ExtraSearchPaths { get; init; } = [];

    /// <summary>Where to read about it when Kontena cannot install it for you.</summary>
    public string? DocumentationUrl { get; init; }
}
