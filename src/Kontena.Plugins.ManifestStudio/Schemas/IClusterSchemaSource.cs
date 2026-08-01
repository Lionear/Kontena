using Kontena.Sdk.Orchestration;

namespace Kontena.Plugins.ManifestStudio.Schemas;

/// <summary>
/// The two facts <see cref="SchemaIndex"/> needs from a cluster, carved out of
/// <see cref="IClusterEngine"/>'s much larger OAL surface — a test double for this is two methods,
/// not thirty.
/// </summary>
public interface IClusterSchemaSource
{
    /// <summary>The server version to key the schema cache on (Plan §3 — "cache per serverversie").</summary>
    ValueTask<string> GetServerVersionAsync(CancellationToken ct = default);

    ValueTask<string?> GetOpenApiSchemaAsync(string group, string version, CancellationToken ct = default);
}

/// <summary>Adapts any real <see cref="IClusterEngine"/> the host hands the plugin to what the schema
/// index needs — the only thing this class does is narrow the interface.</summary>
public sealed class ClusterEngineSchemaSource(IClusterEngine engine) : IClusterSchemaSource
{
    public async ValueTask<string> GetServerVersionAsync(CancellationToken ct = default) =>
        (await engine.GetInfoAsync(ct).ConfigureAwait(false)).Version;

    public ValueTask<string?> GetOpenApiSchemaAsync(string group, string version, CancellationToken ct = default) =>
        engine.GetOpenApiSchemaAsync(group, version, ct);
}
