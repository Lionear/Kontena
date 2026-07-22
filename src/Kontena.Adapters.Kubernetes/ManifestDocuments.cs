using k8s;

namespace Kontena.Adapters.Kubernetes;

/// <summary>One document from a bundle: either its decoded content, or why it could not be read.</summary>
/// <param name="Content">The decoded mapping, or null when <paramref name="Error"/> is set.</param>
/// <param name="Error">Parse failure, reported as a failed resource rather than aborting the bundle.</param>
internal sealed record ManifestDocument(Dictionary<string, object?>? Content, string? Error);

/// <summary>
/// Splits a multi-document YAML bundle and decodes each document into a plain map.
/// <para>
/// Deliberately untyped: the apply path addresses every kind generically, so decoding into concrete
/// model classes would both limit it to kinds the client library knows and drop fields it does not
/// model. A map round-trips whatever the user wrote, custom resources included.
/// </para>
/// </summary>
internal static class ManifestDocuments
{
    public static IEnumerable<ManifestDocument> Split(string yaml)
    {
        foreach (var chunk in SplitDocuments(yaml))
        {
            if (string.IsNullOrWhiteSpace(chunk))
                continue;

            ManifestDocument document;
            try
            {
                var decoded = KubernetesYaml.Deserialize<Dictionary<object, object>>(chunk);
                document = decoded is null || decoded.Count == 0
                    ? new ManifestDocument(null, "Invalid manifest: the document is empty.")
                    : new ManifestDocument(ToStringKeyed(decoded), null);
            }
            catch (Exception ex)
            {
                document = new ManifestDocument(null, $"Invalid YAML: {ex.Message}");
            }

            yield return document;
        }
    }

    /// <summary>
    /// The namespaces a bundle creates. Used to explain why a dry-run cannot preview resources that
    /// target them — nothing is persisted, so the namespace is not there when the next document
    /// is validated.
    /// </summary>
    public static IReadOnlySet<string> NamespacesCreatedBy(IEnumerable<ManifestDocument> documents)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            if (document.Content is not { } content)
                continue;

            if (content.TryGetValue("kind", out var kind) && kind?.ToString() == "Namespace" &&
                content.TryGetValue("metadata", out var meta) && meta is IDictionary<string, object?> metadata &&
                metadata.TryGetValue("name", out var name) && name?.ToString() is { Length: > 0 } text)
            {
                names.Add(text);
            }
        }

        return names;
    }

    /// <summary>
    /// Re-key the YAML reader's <c>object</c>-keyed maps as strings. Necessary, not cosmetic: the
    /// patch body is serialized as JSON, and a JSON object cannot have non-string keys.
    /// </summary>
    public static object? ToStringKeyed(object? value) => value switch
    {
        IDictionary<object, object> map => map.ToDictionary(
            e => e.Key?.ToString() ?? string.Empty, e => ToStringKeyed(e.Value), StringComparer.Ordinal),
        IList<object> list => list.Select(ToStringKeyed).ToList(),
        _ => value,
    };

    private static Dictionary<string, object?> ToStringKeyed(IDictionary<object, object> map) =>
        (Dictionary<string, object?>)ToStringKeyed((object)map)!;

    /// <summary>
    /// Split on document markers. Only a line that is exactly <c>---</c> separates documents; the
    /// same characters inside a block scalar do not.
    /// </summary>
    private static IEnumerable<string> SplitDocuments(string yaml)
    {
        var current = new List<string>();
        foreach (var line in yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.TrimEnd() == "---")
            {
                yield return string.Join('\n', current);
                current.Clear();
                continue;
            }

            current.Add(line);
        }

        yield return string.Join('\n', current);
    }
}
