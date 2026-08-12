using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Plugins.ManifestStudio.Schemas;

/// <summary>
/// The common case an editor tab needs: validate one document against its own declared apiVersion/kind,
/// using one already-resolved schema — without the caller having to build a whole bundle-wide schema
/// dictionary just to check the single kind it is looking at right now.
/// </summary>
public static class SingleDocumentDiagnostics
{
    public static IReadOnlyList<Diagnostic> Validate(string document, JsonSchemaNode? schema)
    {
        if (schema is null)
            return [];

        if (ManifestKind.Of(document) is not { } declared)
            return [];

        var schemas = new Dictionary<GroupVersionKind, JsonSchemaNode?> { [declared] = schema };

        return ManifestDiagnostics.Validate(document, schemas);
    }
}
