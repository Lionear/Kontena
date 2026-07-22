using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Kontena.Adapters.Kubernetes;

/// <summary>
/// Strips server-managed noise from an object before it is diffed, and renders it in a stable key
/// order.
/// <para>
/// Without this a diff is unreadable: every apply changes <c>resourceVersion</c>, bumps
/// <c>managedFields</c> timestamps and rewrites <c>status</c>, so a resource with no real change
/// would still show dozens of differing lines. What is left after stripping is the desired state —
/// which is exactly what "what would this apply change?" means.
/// </para>
/// </summary>
internal static class ManifestNormalizer
{
    /// <summary>Metadata the server owns; comparing it says nothing about the user's intent.</summary>
    private static readonly string[] ServerMetadata =
    [
        "managedFields",
        "resourceVersion",
        "generation",
        "uid",
        "creationTimestamp",
        "selfLink",
    ];

    /// <summary>Annotations written by tooling rather than by the author.</summary>
    private static readonly string[] ToolingAnnotations =
    [
        "kubectl.kubernetes.io/last-applied-configuration",
        "deployment.kubernetes.io/revision",
    ];

    /// <summary>
    /// Normalize a decoded object into comparable YAML. Accepts whatever the API client hands back —
    /// its own object graph, or a decoded YAML map — and re-keys it to strings first.
    /// </summary>
    public static string ToComparableYaml(object? decoded)
    {
        if (Rekey(decoded) is not Dictionary<string, object?> map)
            return string.Empty;

        var builder = new StringBuilder();
        Write(builder, Sort(Clean(map)), indent: 0);
        return builder.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// Render the normalized tree ourselves rather than handing it to the YAML serializer. Boxed
    /// numbers come back out of that as doubles — "replicas: 2.0" — which is both wrong-looking in a
    /// diff and unstable to compare. Writing it here keeps the output predictable, which is the
    /// whole requirement for text that gets diffed.
    /// </summary>
    private static void Write(StringBuilder sb, object? value, int indent)
    {
        var pad = new string(' ', indent);

        switch (value)
        {
            case IDictionary<string, object?> map:
                foreach (var (key, item) in map)
                {
                    if (IsScalar(item))
                    {
                        sb.Append(pad).Append(key).Append(": ").Append(Scalar(item)).Append('\n');
                    }
                    else if (item is IList<object?> { Count: 0 } or IDictionary<string, object?> { Count: 0 })
                    {
                        sb.Append(pad).Append(key).Append(item is IList<object?> ? ": []" : ": {}").Append('\n');
                    }
                    else
                    {
                        sb.Append(pad).Append(key).Append(":\n");
                        Write(sb, item, indent + 2);
                    }
                }

                break;

            case IList<object?> list:
                foreach (var item in list)
                {
                    if (IsScalar(item))
                    {
                        sb.Append(pad).Append("- ").Append(Scalar(item)).Append('\n');
                    }
                    else
                    {
                        // Render the entry indented, then fold its first line onto the dash.
                        var nested = new StringBuilder();
                        Write(nested, item, indent + 2);
                        var text = nested.ToString();
                        sb.Append(pad).Append("- ").Append(text.AsSpan(indent + 2));
                    }
                }

                break;
        }
    }

    private static bool IsScalar(object? value) =>
        value is null or string or bool or int or long or double or decimal;

    private static string Scalar(object? value) => value switch
    {
        null => "null",
        bool flag => flag ? "true" : "false",
        string text => Quote(text),
        double d when d == Math.Floor(d) && !double.IsInfinity(d) => ((long)d).ToString(CultureInfo.InvariantCulture),
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>Quote only where a bare scalar would read as something else.</summary>
    private static string Quote(string text)
    {
        if (text.Length == 0)
            return "\"\"";

        // A colon only makes a scalar ambiguous when a space follows it (or it ends the value):
        // "nginx:1.27-alpine" is a perfectly good bare scalar, and quoting it makes diffs read worse.
        var ambiguousColon = text.Contains(": ", StringComparison.Ordinal) || text.EndsWith(':');
        var needsQuotes = ambiguousColon ||
                          text.Any(c => c is '#' or '\n' or '"' or '\'' or '{' or '[' or '&' or '*') ||
                          char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1]) ||
                          bool.TryParse(text, out _) || double.TryParse(text, CultureInfo.InvariantCulture, out _);

        return needsQuotes ? "\"" + text.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : text;
    }

    /// <summary>
    /// Bring any shape the client returns onto one footing: JSON elements, object-keyed YAML maps
    /// and plain maps all become string-keyed dictionaries.
    /// </summary>
    private static object? Rekey(object? value) => value switch
    {
        JsonElement json => FromJson(json),
        IDictionary<string, object?> map => map.ToDictionary(e => e.Key, e => Rekey(e.Value), StringComparer.Ordinal),
        IDictionary<object, object> map => map.ToDictionary(
            e => e.Key?.ToString() ?? string.Empty, e => Rekey(e.Value), StringComparer.Ordinal),
        IList<object> list => list.Select(Rekey).ToList(),
        _ => value,
    };

    private static object? FromJson(JsonElement json) => json.ValueKind switch
    {
        JsonValueKind.Object => json.EnumerateObject()
            .ToDictionary(p => p.Name, p => FromJson(p.Value), StringComparer.Ordinal),
        JsonValueKind.Array => json.EnumerateArray().Select(FromJson).ToList(),
        JsonValueKind.String => json.GetString(),
        JsonValueKind.Number => json.TryGetInt64(out var l) ? l : json.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static Dictionary<string, object?> Clean(Dictionary<string, object?> root)
    {
        var copy = new Dictionary<string, object?>(root, StringComparer.Ordinal);

        // Status is reported by controllers, never applied by the user.
        copy.Remove("status");

        if (copy.TryGetValue("metadata", out var metadataValue) &&
            metadataValue is IDictionary<string, object?> metadata)
        {
            var cleanedMetadata = new Dictionary<string, object?>(metadata, StringComparer.Ordinal);
            foreach (var key in ServerMetadata)
                cleanedMetadata.Remove(key);

            if (cleanedMetadata.TryGetValue("annotations", out var annotationsValue) &&
                annotationsValue is IDictionary<string, object?> annotations)
            {
                var cleanedAnnotations = new Dictionary<string, object?>(annotations, StringComparer.Ordinal);
                foreach (var key in ToolingAnnotations)
                    cleanedAnnotations.Remove(key);

                if (cleanedAnnotations.Count == 0)
                    cleanedMetadata.Remove("annotations");
                else
                    cleanedMetadata["annotations"] = cleanedAnnotations;
            }

            copy["metadata"] = cleanedMetadata;
        }

        return copy;
    }

    /// <summary>
    /// Recursively order map keys. The API server does not promise a key order, so without this the
    /// same object could diff against itself.
    /// </summary>
    private static object? Sort(object? value) => value switch
    {
        IDictionary<string, object?> map => SortMap(map),
        IList<object?> list => list.Select(Sort).ToList(),
        _ => value,
    };

    private static SortedDictionary<string, object?> SortMap(IDictionary<string, object?> map)
    {
        var sorted = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, item) in map)
            sorted[key] = Sort(item);

        return sorted;
    }
}
