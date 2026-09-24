using System.Text.Json;
using System.Text.Json.Serialization;
using Kontena.Sdk;

namespace Kontena.Engines.Plugins;

/// <summary>
/// What a plugin says about itself, in a file next to its assembly. It mirrors
/// <see cref="Kontena.Sdk.EngineManifest"/>, which exists only as a property on a loaded type — and to
/// <em>ask</em> whether something may run you have to be able to say what it is without running it.
/// <para>
/// The assembly is named rather than derived from the id: ids read <c>com.acme.nerdctl</c> and
/// assemblies read <c>Kontena.Plugins.Nerdctl.dll</c>, and deriving one from the other would tie two
/// naming schemes together that have no reason to agree.
/// </para>
/// </summary>
public sealed record PluginManifest
{
    /// <summary>Stable unique id, e.g. "com.acme.nerdctl".</summary>
    public required string Id { get; init; }

    /// <summary>Human-facing name.</summary>
    public required string Name { get; init; }

    /// <summary>Semantic version of the plugin.</summary>
    public required string Version { get; init; }

    /// <summary>
    /// Assembly filename, e.g. "Kontena.Plugins.Nerdctl.dll". This is named rather than derived from
    /// the id: ids and assemblies follow different conventions, and deriving one from the other would
    /// couple them, leaving later maintainers guessing which format changed.
    /// </summary>
    public required string Assembly { get; init; }

    /// <summary>Author or vendor.</summary>
    public string Author { get; init; } = string.Empty;

    /// <summary>Short description of the plugin's purpose.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Minimum Kontena SDK version this plugin targets, as <c>major.minor.patch</c>, or empty for no
    /// floor. <see cref="Kontena.Sdk.EngineManifest.MinSdkVersion"/> states the format; the loader
    /// rejects anything that does not match it.
    /// </summary>
    public string MinSdkVersion { get; init; } = string.Empty;

    /// <summary>
    /// The operating systems this plugin runs on, or empty for all of them (KON-280). Read before the
    /// consent question and before the assembly is touched: whether a plugin belongs on this machine is
    /// answerable from this file alone, and asking the user about one that cannot run here is a question
    /// with no useful answer.
    /// </summary>
    public IReadOnlyList<PluginPlatform> Platforms { get; init; } = [];

    /// <summary>
    /// Which kinds of backend this plugin contributes, or empty for none. See
    /// <see cref="Kontena.Sdk.EngineManifest.Backends"/>; <c>PluginLoader</c> checks the assembly against it.
    /// </summary>
    public IReadOnlyList<BackendKind> Backends { get; init; } = [];

    /// <summary>
    /// The external command-line tools this plugin runs, by name — <c>["git"]</c> (KON-438). Empty for a
    /// plugin that shells out to nothing.
    /// <para>
    /// Names here where <see cref="Kontena.Sdk.EngineManifest.Tools"/> carries whole
    /// <see cref="Kontena.Sdk.Tooling.ExternalTool"/> descriptions, and the asymmetry is the point. This
    /// file is read before the user has agreed to anything, so what it is for is the sentence in the
    /// consent dialog: "this plugin runs git". How to find git, how to ask its version and where to
    /// download it are answers only the assembly gives, after consent — a download location read out of
    /// an unvetted text file would be the host fetching and running a binary a stranger named.
    /// </para>
    /// <para>
    /// <c>PluginLoader</c> holds the assembly to this in one direction: a tool the assembly describes and
    /// this file does not name is a rejection, because the user agreed to a list they were shown.
    /// Naming more than the assembly ships is fine — a plugin that drops a tool between versions is not
    /// lying about anything. Nothing here <em>stops</em> a loaded plugin running any command it likes;
    /// like <see cref="Permissions"/>, it is a claim by a named author, made checkable where it can be.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Tools { get; init; } = [];

    /// <summary>Whether this plugin contributes pages of its own. See
    /// <see cref="Kontena.Sdk.EngineManifest.ContributesUi"/>.</summary>
    public bool ContributesUi { get; init; }

    /// <summary>
    /// What this plugin says it will do, in the plugin author's own words, one line per capability
    /// (KON-296). The consent dialog <em>renders</em> these; it does not compose them, and nothing here
    /// enforces them — an in-process plugin can do whatever the host can.
    /// <para>
    /// That is exactly why they are worth showing: what a user agrees to is a claim by a named author
    /// about a signed build (KON-79), and a claim you never see is not one you can hold anyone to. The
    /// structured, checkable version of this is KON-79's capability declaration, which is deliberately
    /// still unbuilt: it is the one part of a manifest that cannot change after publication without
    /// breaking every plugin already out there, so it waits for the trust model that gives it teeth.
    /// KON-280 left room for it and built the rest.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Permissions { get; init; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,

        // BackendKind is written by hand in plugin.json, so it is written as "engine"/"cluster" rather
        // than as the ordinal of an enum member whose order is ours to change.
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Read a manifest, or say why not. Returns null rather than throwing: every caller here is
    /// scanning a directory it does not control, and "this one is not a plugin" is an ordinary answer.
    /// </summary>
    public static PluginManifest? Read(string path, out string? reason)
    {
        try
        {
            if (!File.Exists(path))
            {
                reason = "No plugin.json";
                return null;
            }

            var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path), Options);
            if (manifest is null)
            {
                reason = "plugin.json is empty";
                return null;
            }

            reason = null;
            return manifest;
        }
        catch (Exception ex)
        {
            reason = "Unreadable plugin.json: " + ex.Message;
            return null;
        }
    }
}
