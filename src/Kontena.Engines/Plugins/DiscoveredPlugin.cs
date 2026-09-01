using Kontena.Sdk;
using Kontena.Sdk.Tooling;

namespace Kontena.Engines.Plugins;

/// <summary>What happened to one directory under the plugins root.</summary>
public enum PluginStatus
{
    /// <summary>Loaded, and its providers are in <see cref="DiscoveredPlugin.Providers"/>.</summary>
    Loaded,

    /// <summary>Found and readable, but the user has not agreed to run this build of it.</summary>
    AwaitingConsent,

    /// <summary>Not usable. <see cref="DiscoveredPlugin.Reason"/> says why.</summary>
    Rejected,
}

/// <summary>
/// What a directory claims to be, plus what its assembly actually is — the pair a consent answer is
/// given to (KON-362).
/// <para>
/// The manifest alone is not enough to decide by: it is a text file next to the code it describes, and
/// anything that can replace the code can leave the text as it was. So the digest of the assembly
/// travels with it, and the answer is recorded against both.
/// </para>
/// </summary>
/// <param name="Manifest">What the directory says about itself.</param>
/// <param name="Sha256">
/// Lowercase hex SHA-256 of the assembly, or empty when it could not be read — no recorded answer
/// matches an empty digest, so an unreadable assembly is never treated as one that was agreed to.
/// </param>
public sealed record PluginCandidate(PluginManifest Manifest, string Sha256);

/// <summary>
/// One directory under the plugins root and what became of it. Failures are values here rather than
/// exceptions: a plugin that cannot load is a thing to report in Settings, not a reason for the app
/// not to start.
/// </summary>
/// <param name="Directory">The directory scanned, so a report can name it.</param>
/// <param name="Manifest">What it said about itself, or null when that could not be read.</param>
/// <param name="Status">The outcome.</param>
/// <param name="Reason">Why, when the outcome is <see cref="PluginStatus.Rejected"/>.</param>
/// <param name="Providers">The backends it contributed, when it loaded.</param>
public sealed record DiscoveredPlugin(
    string Directory,
    PluginManifest? Manifest,
    PluginStatus Status,
    string? Reason,
    IReadOnlyList<IBackendProvider> Providers)
{
    /// <summary>
    /// The pages it contributed, when it loaded (KON-331). Init-only rather than positional because
    /// every rejection above already says "no providers" with an empty list, and a second empty list on
    /// each of those says nothing a reader did not know.
    /// </summary>
    public IReadOnlyList<PluginPage> Pages { get; init; } = [];

    /// <summary>
    /// The external tools it declared, when it loaded (KON-438) — what Settings &#8250; Tools adds to its
    /// own list, so a plugin's <c>git</c> gets the detection and version check kubectl gets. Only the
    /// ones <c>plugin.json</c> also names; the loader rejects the plugin outright over the rest.
    /// </summary>
    public IReadOnlyList<ExternalTool> Tools { get; init; } = [];

    /// <summary>
    /// The digest the consent question was asked about, so the answer can be recorded against the
    /// bytes rather than against the name (KON-362). Empty when the assembly could not be read.
    /// </summary>
    public string Sha256 { get; init; } = string.Empty;
}
