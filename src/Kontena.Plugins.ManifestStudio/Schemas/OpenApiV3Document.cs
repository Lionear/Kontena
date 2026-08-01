using System.Text.Json;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Plugins.ManifestStudio.Schemas;

/// <summary>
/// One parsed <c>/openapi/v3/...</c> document for a single API group+version. Finds a kind's schema
/// by its <c>x-kubernetes-group-version-kind</c> extension — the same rule KON-86 already applies to
/// the plural: discovered, never guessed from the kind's name.
/// <para>
/// <c>allOf</c>/<c>oneOf</c>/<c>anyOf</c> composition are not resolved (ponytail: some CRDs use it for
/// their schema, core resources do not) — a schema built from one degrades to a bare <c>"object"</c>
/// with no properties, which is the same honest "unverifiable" state as a group the cluster does not
/// serve at all (Plan §3). Add composition support if a real CRD needs completion inside one.
/// </para>
/// </summary>
public sealed class OpenApiV3Document
{
    private readonly JsonDocument _document;
    private readonly Dictionary<string, JsonElement> _schemasByName;

    // Registered before a node's properties are filled in, so a schema that refers back to itself
    // (Kubernetes' JSONSchemaProps does, for nested CRD validation) returns the same instance instead
    // of recursing forever.
    private readonly Dictionary<string, JsonSchemaNode> _resolved = new(StringComparer.Ordinal);

    private OpenApiV3Document(JsonDocument document, Dictionary<string, JsonElement> schemasByName)
    {
        _document = document;
        _schemasByName = schemasByName;
    }

    public static OpenApiV3Document Parse(string json)
    {
        var document = JsonDocument.Parse(json);
        var schemas = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        if (document.RootElement.TryGetProperty("components", out var components)
            && components.TryGetProperty("schemas", out var schemasElement))
        {
            foreach (var schema in schemasElement.EnumerateObject())
                schemas[schema.Name] = schema.Value;
        }

        return new OpenApiV3Document(document, schemas);
    }

    /// <summary>The schema for one kind, or null when this document does not describe it at all.</summary>
    public JsonSchemaNode? Resolve(GroupVersionKind kind)
    {
        foreach (var (name, schema) in _schemasByName)
        {
            if (!schema.TryGetProperty("x-kubernetes-group-version-kind", out var gvks))
                continue;

            foreach (var gvk in gvks.EnumerateArray())
            {
                var group = gvk.TryGetProperty("group", out var g) ? g.GetString() ?? "" : "";
                var version = gvk.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
                var resolvedKind = gvk.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";

                if (group == kind.Group && version == kind.Version && resolvedKind == kind.Kind)
                    return ResolveRef(name);
            }
        }

        return null;
    }

    private JsonSchemaNode ResolveRef(string schemaName)
    {
        if (_resolved.TryGetValue(schemaName, out var existing))
            return existing;

        var node = new JsonSchemaNode();
        _resolved[schemaName] = node;

        if (_schemasByName.TryGetValue(schemaName, out var schema))
            Fill(node, schema);

        return node;
    }

    /// <summary>A property or array-items schema: either a bare <c>$ref</c> (the common case) or an
    /// inline definition. Only a named <c>$ref</c> needs the cycle guard in <see cref="ResolveRef"/> —
    /// Kubernetes' generated schemas never nest a bare <c>$ref</c> inside more schema.</summary>
    private JsonSchemaNode ResolveInline(JsonElement schema)
    {
        if (schema.TryGetProperty("$ref", out var reference))
            return ResolveRef(RefName(reference.GetString()!));

        var node = new JsonSchemaNode();
        Fill(node, schema);
        return node;
    }

    private void Fill(JsonSchemaNode node, JsonElement schema)
    {
        if (schema.TryGetProperty("type", out var type) && type.GetString() is { } typeName)
            node.Type = typeName;

        if (schema.TryGetProperty("description", out var description))
            node.Description = description.GetString();

        if (schema.TryGetProperty("enum", out var enumValues))
            node.Enum = [.. enumValues.EnumerateArray().Select(e => e.GetString() ?? "")];

        if (schema.TryGetProperty("required", out var required))
            foreach (var name in required.EnumerateArray())
                node.Required.Add(name.GetString()!);

        if (schema.TryGetProperty("properties", out var properties))
            foreach (var property in properties.EnumerateObject())
                node.Properties[property.Name] = ResolveInline(property.Value);

        if (schema.TryGetProperty("items", out var items))
            node.Items = ResolveInline(items);
    }

    /// <summary><c>"#/components/schemas/io.k8s.api.apps.v1.Deployment"</c> → the name after the last slash.</summary>
    private static string RefName(string reference) => reference[(reference.LastIndexOf('/') + 1)..];
}
