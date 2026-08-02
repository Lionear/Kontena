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
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string Assembly { get; init; }
    public string Author { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
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
