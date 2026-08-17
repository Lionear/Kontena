namespace Kontena.Sdk.Tooling;

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

    /// <summary>
    /// What this tool is for, in the user's terms rather than the project's own blurb. Belongs to the
    /// tool rather than to the page showing it: a tools page that looked its purpose up in a dictionary
    /// of its own threw <see cref="KeyNotFoundException"/> the moment the list grew (KON-266).
    /// </summary>
    public string Purpose { get; init; } = string.Empty;

    /// <summary>
    /// Where Kontena can fetch this tool itself, on a machine with no package manager. Null for tools
    /// Kontena will not download — which is most of them: this is only for the ones it drives to build
    /// something, and only where the publisher ships a checksum to verify against.
    /// </summary>
    public ToolReleaseSpec? Release { get; init; }

    /// <summary>
    /// The oldest release Kontena works with, or null when any version will do. Older than this is a
    /// warning rather than a refusal — see <see cref="ToolReadiness"/>.
    /// </summary>
    public string? MinimumVersion { get; init; }

    /// <summary>
    /// What is actually lost by carrying on with a version older than <see cref="MinimumVersion"/>.
    /// Per tool, because the answer is: kind and minikube ignore the cluster settings Kontena writes,
    /// kubectl renders overlays by older rules. Null falls back to the general wording.
    /// </summary>
    public string? OutdatedConsequence { get; init; }
}
