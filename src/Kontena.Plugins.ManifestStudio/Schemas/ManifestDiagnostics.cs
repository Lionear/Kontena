using System.Globalization;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Plugins.ManifestStudio.Schemas;

public enum DiagnosticSeverity { Error, Warning, Hint }

/// <summary><see cref="Policy"/> is KON-297 — deterministic house rules over the rendered bundle
/// (container resource requests, image tags, required labels, …), evaluated by
/// <c>Kontena.Plugins.ManifestStudio.Policy.PolicyEngine</c> rather than here: unlike the other three,
/// it needs no schema at all, so it stays its own pure function the caller runs alongside this one.</summary>
public enum DiagnosticAuthority { Schema, ClusterDiscovery, CrossDocument, Policy }

/// <summary>What kind of finding this is, structured rather than left for a caller to sniff out of
/// <see cref="Diagnostic.Message"/> — <c>QuickFixEngine</c> (KON-292) dispatches on this, not on
/// parsing English sentences.</summary>
public enum DiagnosticKind { Other, UnknownField, MissingRequiredField, WrongType, DeprecatedApiVersion, UnmatchedReference }

public sealed record Diagnostic(DiagnosticAuthority Authority, DiagnosticSeverity Severity, int Line, string Message)
{
    public DiagnosticKind Kind { get; init; } = DiagnosticKind.Other;

    /// <summary>The field name involved, for <see cref="DiagnosticKind.UnknownField"/>,
    /// <see cref="DiagnosticKind.MissingRequiredField"/>, <see cref="DiagnosticKind.WrongType"/> and
    /// <see cref="DiagnosticKind.UnmatchedReference"/>.</summary>
    public string? FieldName { get; init; }

    /// <summary>The version the cluster serves instead, for <see cref="DiagnosticKind.DeprecatedApiVersion"/>.</summary>
    public string? SuggestedVersion { get; init; }
}

/// <summary>
/// Validates a bundle (one or more <c>---</c>-separated documents — the same shape as
/// <c>ManifestBundle.Yaml</c>) against three authorities that never get to speak for one another
/// (Plan §5):
/// <list type="bullet">
/// <item><b>schema</b> — unknown field, wrong type, missing required field. Error.</item>
/// <item><b>cluster discovery</b> — this apiVersion no longer serves this cluster. Warning.</item>
/// <item><b>cross-document</b> — a reference not found in the bundle. Hint, never error: the bundle is
/// not the cluster, and referring to something applied earlier is legitimate.</item>
/// </list>
/// Admission/webhooks are deliberately out of scope — only the apiserver can answer those, at dry-run
/// time (KON-86); guessing locally is exactly the false certainty that ticket avoided.
/// <para>
/// A pure function of already-resolved schemas, same as <see cref="CompletionEngine"/>: the caller
/// fetches from <see cref="SchemaIndex"/> (async, one call per distinct group+version in the bundle)
/// before calling <see cref="Validate"/>, which does no I/O of its own.
/// </para>
/// </summary>
public static class ManifestDiagnostics
{
    /// <summary>
    /// ponytail: a hand-picked, narrow set of the reference fields that actually show up in real
    /// manifests (env/envFrom's *Ref, and volumes' configMap/secret). Not every way a manifest can name
    /// another object — extend this table as a real one turns up unflagged, rather than trying to
    /// enumerate the whole API surface up front.
    /// </summary>
    private static readonly Dictionary<string, string> ReferenceFieldKinds = new(StringComparer.Ordinal)
    {
        ["configMapKeyRef"] = "ConfigMap",
        ["configMapRef"] = "ConfigMap",
        ["configMap"] = "ConfigMap",
        ["secretKeyRef"] = "Secret",
        ["secretRef"] = "Secret",
        ["secret"] = "Secret",
    };

    public static IReadOnlyList<Diagnostic> Validate(
        string bundle,
        IReadOnlyDictionary<GroupVersionKind, JsonSchemaNode?> schemas,
        IReadOnlyList<ApiResource>? discoveredResources = null)
    {
        var documents = SplitDocuments(bundle)
            .Select(d => (d.LineOffset, Outline: YamlOutline.Parse(d.Text)))
            .ToArray();

        var knownNames = documents
            .Select(d => (Kind: TopLevelValue(d.Outline, "kind"), Name: MetadataName(d.Outline)))
            .Where(x => x.Kind is not null && x.Name is not null)
            .Select(x => (x.Kind!, x.Name!))
            .ToHashSet();

        var diagnostics = new List<Diagnostic>();

        foreach (var (lineOffset, outline) in documents)
        {
            var apiVersion = TopLevelValue(outline, "apiVersion");
            var kind = TopLevelValue(outline, "kind");
            if (apiVersion is null || kind is null)
                continue; // Not enough here to resolve a schema against at all.

            var (group, version) = SplitApiVersion(apiVersion);
            var schema = schemas.GetValueOrDefault(new GroupVersionKind(group, version, kind));

            CheckClusterDiscovery(group, version, kind, lineOffset, discoveredResources, schema is not null, diagnostics);
            ValidateNode(outline, schema, lineOffset, diagnostics);
            CheckReferences(outline, lineOffset, knownNames, diagnostics);
        }

        return diagnostics;
    }

    // ── Schema authority ─────────────────────────────────────────────────────

    private static void ValidateNode(YamlOutline node, JsonSchemaNode? schema, int lineOffset, List<Diagnostic> diagnostics)
    {
        if (schema is null)
            return; // Unverifiable, not an error (Plan §3) — propagates silently down the whole subtree.

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var child in node.Children)
        {
            if (child.IsArrayItem)
            {
                ValidateNode(child, schema.Items, lineOffset, diagnostics);
                continue;
            }

            if (child.Key is not { } key)
                continue;

            seen.Add(key);

            if (!schema.Properties.TryGetValue(key, out var property))
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticAuthority.Schema, DiagnosticSeverity.Error, lineOffset + child.Line,
                    $"Unknown field '{key}'.")
                { Kind = DiagnosticKind.UnknownField, FieldName = key });
                continue;
            }

            if (child.InlineValue is { } value && !TypeMatches(property.Type, value))
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticAuthority.Schema, DiagnosticSeverity.Error, lineOffset + child.Line,
                    $"'{key}' expects {property.Type}, got '{value}'.")
                { Kind = DiagnosticKind.WrongType, FieldName = key });
            }

            ValidateNode(child, property, lineOffset, diagnostics);
        }

        foreach (var required in schema.Required)
        {
            if (seen.Contains(required))
                continue;

            diagnostics.Add(new Diagnostic(
                DiagnosticAuthority.Schema, DiagnosticSeverity.Error, lineOffset + Math.Max(node.Line, 0),
                $"Missing required field '{required}'.")
            { Kind = DiagnosticKind.MissingRequiredField, FieldName = required });
        }
    }

    private static bool TypeMatches(string type, string value) => type switch
    {
        "integer" => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
        "number" => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
        "boolean" => value is "true" or "false",
        // object/array/string are not checked here: "{}" and "{matchLabels: {app: web}}" are both valid
        // flow-style objects that look identical to a scalar at this level (YamlOutline never parses
        // flow style), so flagging "object" would false-positive on the common empty-object shorthand.
        // Guessing wrong here costs more than staying quiet (same reasoning as skipping allOf/oneOf).
        _ => true,
    };

    // ── Cluster discovery authority ──────────────────────────────────────────

    private static void CheckClusterDiscovery(
        string group, string version, string kind, int lineOffset,
        IReadOnlyList<ApiResource>? discovered, bool schemaFound, List<Diagnostic> diagnostics)
    {
        if (schemaFound || discovered is null)
            return; // Confirmed-good, or nothing to compare against — both stay silent.

        var servedVersions = discovered
            .Where(r => r.Kind.Group == group && r.Kind.Kind == kind && r.Kind.Version != version)
            .Select(r => r.Kind.Version)
            .Distinct()
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToArray();

        if (servedVersions.Length == 0)
            return; // The kind itself is not known either — unverifiable, not a version mismatch.

        var groupLabel = group.Length == 0 ? version : $"{group}/{version}";
        diagnostics.Add(new Diagnostic(
            DiagnosticAuthority.ClusterDiscovery, DiagnosticSeverity.Warning, lineOffset,
            $"This cluster no longer serves {kind} at {groupLabel} — it serves "
            + string.Join(", ", servedVersions) + " instead.")
        { Kind = DiagnosticKind.DeprecatedApiVersion, SuggestedVersion = servedVersions[0] });
    }

    // ── Cross-document authority ─────────────────────────────────────────────

    private static void CheckReferences(
        YamlOutline node, int lineOffset, HashSet<(string Kind, string Name)> knownNames, List<Diagnostic> diagnostics)
    {
        foreach (var child in node.Children)
        {
            if (child.Key is { } key && ReferenceFieldKinds.TryGetValue(key, out var resourceKind))
            {
                var name = child.Children.FirstOrDefault(c => c.Key == "name");
                if (name?.InlineValue is { } referenced && !knownNames.Contains((resourceKind, referenced)))
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticAuthority.CrossDocument, DiagnosticSeverity.Hint, lineOffset + name.Line,
                        $"No {resourceKind} named '{referenced}' found in this bundle.")
                    { Kind = DiagnosticKind.UnmatchedReference, FieldName = referenced });
                }
            }

            CheckReferences(child, lineOffset, knownNames, diagnostics);
        }
    }

    // ── Bundle / document helpers ────────────────────────────────────────────

    private static string? TopLevelValue(YamlOutline outline, string key) =>
        outline.Children.FirstOrDefault(c => c.Key == key)?.InlineValue;

    private static string? MetadataName(YamlOutline outline) =>
        outline.Children.FirstOrDefault(c => c.Key == "metadata") is { } metadata
            ? TopLevelValue(metadata, "name")
            : null;

    /// <summary>Split for reuse by <see cref="SingleDocumentDiagnostics"/> — one document's own
    /// apiVersion is exactly this split, done once here rather than twice.</summary>
    internal static (string Group, string Version) SplitApiVersion(string apiVersion)
    {
        var slash = apiVersion.IndexOf('/');
        return slash < 0 ? (string.Empty, apiVersion) : (apiVersion[..slash], apiVersion[(slash + 1)..]);
    }

    /// <summary>Internal, not private: <c>Kustomize.PatchMatchChecker</c> (KON-293) needs the same
    /// per-document split to read a rendered bundle's kind/name pairs.</summary>
    internal static IEnumerable<(string Text, int LineOffset)> SplitDocuments(string bundle)
    {
        var lines = bundle.Split('\n');
        var start = 0;

        for (var i = 0; i <= lines.Length; i++)
        {
            if (i < lines.Length && lines[i].TrimEnd('\r').Trim() != "---")
                continue;

            if (i > start)
                yield return (string.Join('\n', lines[start..i]), start);

            start = i + 1;
        }
    }
}
