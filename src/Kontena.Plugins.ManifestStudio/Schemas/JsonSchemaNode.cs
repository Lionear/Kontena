namespace Kontena.Plugins.ManifestStudio.Schemas;

/// <summary>
/// One resolved node of an OpenAPI v3 schema — a field's type and, if it is an object, what its own
/// fields are. "Resolved" means any <c>$ref</c> has already been followed; nothing downstream of
/// <see cref="OpenApiV3Document"/> ever sees one.
/// <para>
/// A class, not a record: a self-referential schema (Kubernetes' own <c>JSONSchemaProps</c>, used to
/// describe a CRD's own schema) needs a node it can point back at before that node's properties exist
/// yet — see <see cref="OpenApiV3Document"/>'s cycle guard. An immutable record has no way to be handed
/// out before it is finished.
/// </para>
/// </summary>
public sealed class JsonSchemaNode
{
    /// <summary>OpenAPI's own vocabulary: "object", "array", "string", "integer", "boolean", "number".</summary>
    public string Type { get; internal set; } = "object";

    public string? Description { get; internal set; }

    /// <summary>Only set for an enum-constrained string field.</summary>
    public IReadOnlyList<string>? Enum { get; internal set; }

    /// <summary>Property name → its schema. Empty for anything that is not <c>"object"</c>.</summary>
    public Dictionary<string, JsonSchemaNode> Properties { get; } = new(StringComparer.Ordinal);

    /// <summary>Which of <see cref="Properties"/> the schema marks required.</summary>
    public HashSet<string> Required { get; } = new(StringComparer.Ordinal);

    /// <summary>The element schema, for <c>"array"</c>; null otherwise.</summary>
    public JsonSchemaNode? Items { get; internal set; }
}
