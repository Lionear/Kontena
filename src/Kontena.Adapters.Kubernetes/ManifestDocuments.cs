using k8s;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Adapters.Kubernetes;

/// <summary>One document from a bundle: either its decoded content, or why it could not be read.</summary>
/// <param name="Content">The decoded mapping, or null when <paramref name="Error"/> is set.</param>
/// <param name="Error">Parse failure, reported as a failed resource rather than aborting the bundle.</param>
internal sealed record ManifestDocument(Dictionary<string, object?>? Content, string? Error);

/// <summary>
/// What a bundle has to create before the rest of itself makes sense to the API server.
/// </summary>
/// <param name="Namespaces">Namespaces the bundle creates.</param>
/// <param name="CustomKinds">Kinds the bundle's CRDs define, as <c>group/Kind</c>.</param>
internal sealed record BundlePrerequisites(
    IReadOnlySet<string> Namespaces, IReadOnlySet<string> CustomKinds);

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
            if (IsEmpty(chunk))
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
    /// What a bundle brings into existence that the rest of the same bundle needs before it can be
    /// validated at all: the namespaces it creates, and the custom kinds its CRDs define — each as
    /// <c>group/Kind</c>.
    /// <para>
    /// Both are the same problem seen twice. A dry-run persists nothing, so neither is there when the
    /// documents depending on them are validated; a real apply has them only if they went first.
    /// </para>
    /// </summary>
    public static BundlePrerequisites PrerequisitesIn(IEnumerable<ManifestDocument> documents)
    {
        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        var customKinds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var document in documents)
        {
            if (document.Content is not { } content)
                continue;

            switch (KindOf(content)?.Kind)
            {
                case "Namespace" when NameOf(content) is { Length: > 0 } ns:
                    namespaces.Add(ns);
                    break;

                case CustomResourceDefinition when DefinedKind(content) is { } defined:
                    customKinds.Add(defined);
                    break;
            }
        }

        return new BundlePrerequisites(namespaces, customKinds);
    }

    /// <summary>
    /// Whether this document has to go before the rest of the bundle. Only namespaces and CRDs do:
    /// they are the two things the API server refuses to recognise a later document without.
    /// </summary>
    public static bool IsPrerequisite(ManifestDocument document) =>
        document.Content is { } content && KindOf(content)?.Kind is "Namespace" or CustomResourceDefinition;

    /// <summary>The group/version/kind a document declares, or null when it declares neither.</summary>
    public static GroupVersionKind? KindOf(IDictionary<string, object?> content)
    {
        if (Text(content, "kind") is not { Length: > 0 } kind ||
            Text(content, "apiVersion") is not { Length: > 0 } apiVersion)
        {
            return null;
        }

        var slash = apiVersion.LastIndexOf('/');
        return slash < 0
            ? new GroupVersionKind(string.Empty, apiVersion, kind)
            : new GroupVersionKind(apiVersion[..slash], apiVersion[(slash + 1)..], kind);
    }

    private const string CustomResourceDefinition = "CustomResourceDefinition";

    /// <summary>The <c>group/Kind</c> a CRD teaches the cluster to serve.</summary>
    private static string? DefinedKind(IDictionary<string, object?> content)
    {
        if (content.TryGetValue("spec", out var value) is false ||
            value is not IDictionary<string, object?> spec ||
            Text(spec, "group") is not { Length: > 0 } group ||
            spec.TryGetValue("names", out var raw) is false ||
            raw is not IDictionary<string, object?> names ||
            Text(names, "kind") is not { Length: > 0 } kind)
        {
            return null;
        }

        return $"{group}/{kind}";
    }

    private static string? NameOf(IDictionary<string, object?> content) =>
        content.TryGetValue("metadata", out var value) && value is IDictionary<string, object?> metadata
            ? Text(metadata, "name")
            : null;

    private static string? Text(IDictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var value) ? value?.ToString() : null;

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
    /// Whether a chunk holds no resource — blank, or nothing but comments.
    /// <para>
    /// The comment case is <c>helm template</c>'s doing: it writes a <c># Source:</c> header for
    /// every template it renders, and a chart whose CRD files carry their own leading comments ends
    /// up with those headers stranded between two <c>---</c> markers. kube-prometheus-stack produces
    /// ten of them, and reading each as a document turned ten rows of the plan red for a manifest
    /// nobody wrote (KON-380).
    /// </para>
    /// </summary>
    private static bool IsEmpty(string chunk)
    {
        foreach (var line in chunk.Split('\n'))
        {
            var trimmed = line.AsSpan().Trim();
            if (trimmed.Length > 0 && trimmed[0] != '#')
                return false;
        }

        return true;
    }

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
