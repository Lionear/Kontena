namespace Kontena.Sdk;

/// <summary>
/// Metadata for a Kontena extension, used by the plugin loader and the store.
/// <para>
/// It describes a <em>plugin</em>, not only an engine (KON-280): the fields below have to answer what
/// the extension contributes — backends, UI, or both — because a plugin may bring pages and no backend
/// at all (Manifest Studio, <see cref="IUiPlugin"/>, KON-331). The name is the one it was born with.
/// </para>
/// <para>
/// What the host has to be able to decide from this alone, without loading anything: does it run on
/// this machine, does it fit this build's SDK, and what would it add. That is what makes a wizard, a
/// store listing and a Settings page possible without instantiating providers first.
/// </para>
/// </summary>
public sealed record EngineManifest
{
    /// <summary>Stable unique id, e.g. "com.acme.nomad".</summary>
    public required string Id { get; init; }

    /// <summary>Human-facing name.</summary>
    public required string Name { get; init; }

    /// <summary>Semantic version of the extension.</summary>
    public required string Version { get; init; }

    /// <summary>Author or vendor.</summary>
    public string Author { get; init; } = string.Empty;

    /// <summary>Short description shown in the store.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Minimum Kontena SDK version this extension targets, as <c>major.minor.patch</c> — the format is
    /// fixed, and the loader rejects anything else rather than ignoring it (KON-280). Empty means no
    /// floor.
    /// <para>
    /// Three parts exactly: a four-part <c>1.2.3.4</c> and a two-part <c>1.2</c> are both refused,
    /// because a version compared against the SDK assembly's own has to mean one thing. Prerelease and
    /// build suffixes (<c>1.0.0-beta</c>) are not accepted either — there is nothing to compare them
    /// against, since the SDK assembly version has no such part.
    /// </para>
    /// </summary>
    public string MinSdkVersion { get; init; } = string.Empty;

    /// <summary>
    /// The operating systems this extension runs on, or empty for all of them — see
    /// <see cref="PluginPlatform.SupportsHost"/> for what empty means and why it is the default.
    /// <para>
    /// The host filters on this before anything else happens to a plugin: a Windows machine has no use
    /// for an adapter that drives Apple's <c>container</c>, and finding that out by loading it and
    /// watching it fail is the outcome this field exists to avoid.
    /// </para>
    /// </summary>
    public IReadOnlyList<PluginPlatform> Platforms { get; init; } = [];

    /// <summary>
    /// Which kinds of backend this extension contributes — <see cref="BackendKind.Engine"/> for the
    /// CEAL, <see cref="BackendKind.Cluster"/> for the OAL — or empty when it contributes none.
    /// <para>
    /// Declared rather than discovered so the switcher can group and the wizard can filter without
    /// instantiating providers first. The loader holds it to it: an assembly that hands back a kind its
    /// manifest does not name is rejected, because a declaration nothing checks is the state
    /// <see cref="MinSdkVersion"/> spent three releases in.
    /// </para>
    /// <para>
    /// An enum here where <see cref="PluginPlatform.Os"/> is a string, because the two sets differ in
    /// kind: operating systems are the world's and open, backend kinds are this SDK's and closed. A
    /// plugin naming a kind this build has never heard of was built against a newer SDK, which
    /// <see cref="MinSdkVersion"/> already rejects for a better reason.
    /// </para>
    /// </summary>
    public IReadOnlyList<BackendKind> Backends { get; init; } = [];

    /// <summary>
    /// Whether this extension contributes pages of its own (<see cref="IUiPlugin"/>, KON-331). A plugin
    /// may declare this, <see cref="Backends"/>, or both; declaring neither describes a plugin that adds
    /// nothing, which the loader rejects.
    /// </summary>
    public bool ContributesUi { get; init; }
}
