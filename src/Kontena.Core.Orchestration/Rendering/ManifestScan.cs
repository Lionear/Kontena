using System.Globalization;
using Kontena.Core.Orchestration;

namespace Kontena.Core.Orchestration.Rendering;

/// <summary>The identity of one rendered document, as read off the text.</summary>
/// <param name="ApiVersion">The <c>apiVersion</c> field, or empty when absent.</param>
/// <param name="Kind">The <c>kind</c> field, or empty when absent.</param>
/// <param name="Name">The <c>metadata.name</c> field, or empty when absent.</param>
/// <param name="Namespace">The <c>metadata.namespace</c> field, or empty when absent.</param>
/// <param name="Text">The document itself.</param>
public sealed record ScannedDoc(string ApiVersion, string Kind, string Name, string Namespace, string Text)
{
    /// <summary>"apps/v1 Deployment app/payments" — how a document is named in a diagnostic.</summary>
    public string Describe()
    {
        var qualified = Namespace.Length > 0 ? $"{Namespace}/{Name}" : Name;
        return Kind.Length > 0 ? $"{Kind} {qualified}".TrimEnd() : "(document without a kind)";
    }

    /// <summary>What makes two documents the same object — apply would have them fight.</summary>
    public string Identity() =>
        string.Join('|', ApiVersion, Kind, Namespace, Name);
}

/// <summary>
/// A deliberately shallow read of rendered YAML: split into documents and pick out the four
/// fields that identify a resource. It exists to say something useful about a render *before*
/// the cluster sees it — the authoritative parse happens in the adapter, which will reject
/// anything malformed during the dry-run. Advisory, not a YAML implementation.
/// </summary>
public static class ManifestScan
{
    /// <summary>Split a bundle into documents, dropping empty and comment-only ones.</summary>
    public static IReadOnlyList<ScannedDoc> Split(string yaml)
    {
        var docs = new List<ScannedDoc>();
        var current = new List<string>();

        foreach (var line in yaml.ReplaceLineEndings("\n").Split('\n'))
        {
            // A separator only counts at column 0; inside a block scalar everything is indented,
            // so this cannot be confused by a '---' that belongs to embedded content.
            if (line is "---" || line.StartsWith("--- ", StringComparison.Ordinal))
            {
                Flush(docs, current);
                continue;
            }

            current.Add(line);
        }

        Flush(docs, current);
        return docs;
    }

    /// <summary>Static findings over a rendered bundle — what is wrong without asking the cluster.</summary>
    public static IReadOnlyList<RenderDiagnostic> Check(IReadOnlyList<ScannedDoc> docs)
    {
        var found = new List<RenderDiagnostic>();

        if (docs.Count == 0)
        {
            found.Add(new RenderDiagnostic(
                RenderSeverity.Error,
                "The render produced no resources. Check that the source actually emits manifests.",
                Source));
            return found;
        }

        foreach (var doc in docs)
        {
            if (doc.Kind.Length == 0 || doc.ApiVersion.Length == 0)
            {
                found.Add(new RenderDiagnostic(
                    RenderSeverity.Error,
                    $"A document is missing {(doc.Kind.Length == 0 ? "kind" : "apiVersion")} and cannot be applied.",
                    Source));
            }
            else if (doc.Name.Length == 0 && !HasGeneratedName(doc))
            {
                found.Add(new RenderDiagnostic(
                    RenderSeverity.Warning,
                    $"{doc.Kind} has no metadata.name.",
                    Source));
            }
        }

        foreach (var clash in docs
            .Where(d => d.Kind.Length > 0 && d.Name.Length > 0)
            .GroupBy(d => d.Identity(), StringComparer.Ordinal)
            .Where(g => g.Count() > 1))
        {
            found.Add(new RenderDiagnostic(
                RenderSeverity.Error,
                $"{clash.First().Describe()} appears {clash.Count().ToString(CultureInfo.InvariantCulture)} times — "
                    + "applying the bundle would leave whichever copy landed last.",
                Source));
        }

        found.Add(new RenderDiagnostic(
            RenderSeverity.Info,
            Summarise(docs),
            Source));

        return found;
    }

    /// <summary>"14 resources · 3 Deployment, 3 Service, …" — the shape of what was rendered.</summary>
    private static string Summarise(IReadOnlyList<ScannedDoc> docs)
    {
        var kinds = docs
            .Where(d => d.Kind.Length > 0)
            .GroupBy(d => d.Kind, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Count().ToString(CultureInfo.InvariantCulture)} {g.Key}")
            .Take(6);

        var count = docs.Count.ToString(CultureInfo.InvariantCulture);
        var plural = docs.Count == 1 ? "resource" : "resources";
        var breakdown = string.Join(", ", kinds);
        return breakdown.Length > 0 ? $"{count} {plural} · {breakdown}" : $"{count} {plural}";
    }

    private const string Source = "check";

    /// <summary>A generateName is a legitimate way to have no name yet.</summary>
    private static bool HasGeneratedName(ScannedDoc doc) =>
        doc.Text.Contains("generateName:", StringComparison.Ordinal);

    private static void Flush(List<ScannedDoc> docs, List<string> lines)
    {
        var text = string.Join('\n', lines).Trim('\n');
        lines.Clear();

        if (!HasContent(text))
            return;

        docs.Add(new ScannedDoc(
            Top(text, "apiVersion"),
            Top(text, "kind"),
            Nested(text, "metadata", "name"),
            Nested(text, "metadata", "namespace"),
            text));
    }

    /// <summary>A document of nothing but comments and blank lines is not a document.</summary>
    private static bool HasContent(string text) => text
        .Split('\n')
        .Any(l => l.Trim() is { Length: > 0 } t && !t.StartsWith('#'));

    /// <summary>Read a top-level scalar, e.g. <c>kind: Deployment</c>.</summary>
    private static string Top(string text, string key)
    {
        foreach (var line in text.Split('\n'))
        {
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && Match(line, key) is { } value)
                return value;
        }

        return string.Empty;
    }

    /// <summary>Read a scalar one level in, e.g. <c>metadata.name</c>.</summary>
    private static string Nested(string text, string parent, string key)
    {
        var inParent = false;

        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0 || line.Trim().Length == 0)
                continue;

            var indented = char.IsWhiteSpace(line[0]);

            if (!indented)
            {
                // Any other top-level key ends the block we were reading.
                inParent = line.TrimEnd().Equals(parent + ":", StringComparison.Ordinal);
                continue;
            }

            // Only the block's own children count; deeper nesting (labels, annotations) does not.
            if (inParent && line.StartsWith("  ", StringComparison.Ordinal) && !char.IsWhiteSpace(line[2])
                && Match(line.TrimStart(), key) is { } value)
            {
                return value;
            }
        }

        return string.Empty;
    }

    /// <summary>The scalar on <paramref name="line"/> when it sets <paramref name="key"/>.</summary>
    private static string? Match(string line, string key)
    {
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith(key, StringComparison.Ordinal))
            return null;

        var rest = trimmed[key.Length..];
        if (!rest.StartsWith(':'))
            return null;

        return Unquote(rest[1..].Trim());
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
            return value[1..^1];

        return value;
    }
}
