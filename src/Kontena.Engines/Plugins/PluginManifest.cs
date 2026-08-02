using System.Text.Json;
using System.Text.Json.Serialization;

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

    /// <summary>Minimum Kontena SDK version this plugin targets.</summary>
    public string MinSdkVersion { get; init; } = string.Empty;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
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
