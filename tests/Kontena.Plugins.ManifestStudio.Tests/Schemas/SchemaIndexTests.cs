using Kontena.Plugins.ManifestStudio.Schemas;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Plugins.ManifestStudio.Tests.Schemas;

/// <summary>
/// This is the two-method fake <see cref="IClusterSchemaSource"/> exists for: no thirty-member
/// <c>IClusterEngine</c> stub, just the version and the document a test cares about.
/// </summary>
public sealed class FakeClusterSchemaSource(string serverVersion) : IClusterSchemaSource
{
    public int RequestCount { get; private set; }
    public Dictionary<(string Group, string Version), string> Documents { get; } = [];

    public ValueTask<string> GetServerVersionAsync(CancellationToken ct = default) =>
        ValueTask.FromResult(serverVersion);

    public ValueTask<string?> GetOpenApiSchemaAsync(string group, string version, CancellationToken ct = default)
    {
        RequestCount++;
        return ValueTask.FromResult(Documents.GetValueOrDefault((group, version)));
    }
}

public sealed class SchemaIndexTests
{
    private const string PodDocument = """
    {
      "components": {
        "schemas": {
          "io.k8s.api.core.v1.Pod": {
            "type": "object",
            "x-kubernetes-group-version-kind": [{ "group": "", "version": "v1", "kind": "Pod" }]
          }
        }
      }
    }
    """;

    private static readonly GroupVersionKind Pod = new("", "v1", "Pod");

    [Fact]
    public async Task Resolves_a_kind_from_the_matching_group_version_document()
    {
        var source = new FakeClusterSchemaSource("v1.31.0");
        source.Documents[("", "v1")] = PodDocument;
        var index = new SchemaIndex(source);

        var schema = await index.ResolveAsync(Pod);

        Assert.NotNull(schema);
        Assert.Equal("object", schema!.Type);
    }

    [Fact]
    public async Task An_unserved_group_version_is_unverifiable_not_an_exception()
    {
        var source = new FakeClusterSchemaSource("v1.31.0");
        var index = new SchemaIndex(source);

        Assert.Null(await index.ResolveAsync(Pod));
    }

    [Fact]
    public async Task The_same_group_version_document_is_fetched_only_once()
    {
        var source = new FakeClusterSchemaSource("v1.31.0");
        source.Documents[("", "v1")] = PodDocument;
        var index = new SchemaIndex(source);

        await index.ResolveAsync(Pod);
        await index.ResolveAsync(new GroupVersionKind("", "v1", "Node"));
        await index.ResolveAsync(Pod);

        Assert.Equal(1, source.RequestCount);
    }

    [Fact]
    public async Task Two_indexes_on_the_same_server_version_do_not_share_a_cache()
    {
        // Regression guard for the reason the cache lives on the instance and not behind a static
        // field: two fakes that happen to report the same version string must not see each other's
        // documents.
        var first = new FakeClusterSchemaSource("v1.31.0");
        first.Documents[("", "v1")] = PodDocument;

        var second = new FakeClusterSchemaSource("v1.31.0");

        Assert.NotNull(await new SchemaIndex(first).ResolveAsync(Pod));
        Assert.Null(await new SchemaIndex(second).ResolveAsync(Pod));
    }
}
