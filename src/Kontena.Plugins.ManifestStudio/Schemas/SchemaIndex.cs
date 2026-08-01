using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Plugins.ManifestStudio.Schemas;

/// <summary>
/// Resolves a <see cref="GroupVersionKind"/> to its schema, fetching OpenAPI v3 documents from the
/// cluster on demand and caching them per group+version for one server version (Plan §3: reconnecting
/// to a cluster already seen this session costs nothing).
/// <para>
/// The cache lives on the instance, not behind a static field: a static cache keyed by version string
/// would let two <see cref="SchemaIndex"/> instances in the same process (notably two tests) bleed into
/// each other whenever their fakes happen to report the same version.
/// </para>
/// </summary>
public sealed class SchemaIndex(IClusterSchemaSource source)
{
    private readonly Dictionary<string, Dictionary<(string Group, string Version), OpenApiV3Document>> _byServerVersion =
        new(StringComparer.Ordinal);

    /// <summary>
    /// The kind's schema, or null when the cluster does not serve that group+version at all — Plan §3's
    /// "unverifiable", never thrown as an error.
    /// </summary>
    public async ValueTask<JsonSchemaNode?> ResolveAsync(GroupVersionKind kind, CancellationToken ct = default)
    {
        var serverVersion = await source.GetServerVersionAsync(ct).ConfigureAwait(false);

        if (!_byServerVersion.TryGetValue(serverVersion, out var documents))
            _byServerVersion[serverVersion] = documents = [];

        var key = (kind.Group, kind.Version);
        if (!documents.TryGetValue(key, out var document))
        {
            var raw = await source.GetOpenApiSchemaAsync(kind.Group, kind.Version, ct).ConfigureAwait(false);
            if (raw is null)
                return null;

            documents[key] = document = OpenApiV3Document.Parse(raw);
        }

        return document.Resolve(kind);
    }
}
