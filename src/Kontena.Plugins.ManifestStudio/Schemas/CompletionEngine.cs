using Frame = Kontena.Plugins.ManifestStudio.Schemas.YamlLines.Frame;

namespace Kontena.Plugins.ManifestStudio.Schemas;

/// <summary>One thing that could go at the cursor: a field name, or — inside a <c>"key: "</c> — an
/// enum value for that field.</summary>
public sealed record Suggestion(string Name, string Type, bool Required, string? Description);

/// <summary>
/// Suggests what belongs at the cursor, given the schema for the document's kind (Plan §4). A pure
/// function of text, cursor position and schema — no Avalonia reference, no I/O — so it is testable as
/// a suite of textual cases, the same shape as <c>PodDiagnosis</c>/<c>WorkloadTrouble</c> (KON-150).
/// <para>
/// The document is, by definition, invalid while someone is typing in it. Path resolution therefore
/// works off indentation alone, never a full YAML parse: a dangling colon or a half-written key below
/// the cursor never breaks completion, because nothing below the cursor is ever read.
/// </para>
/// <para>
/// ponytail: does not exclude a property already present as a sibling above the cursor — the full
/// property list comes back every time, "name" included even once it has been typed. Add the exclusion
/// once a real editing session makes the repeat suggestion annoying enough to be worth the extra
/// bookkeeping (the ancestry stack below discards sibling names as soon as a new one is seen).
/// </para>
/// </summary>
public static class CompletionEngine
{
    public static IReadOnlyList<Suggestion> Suggest(string document, int position, JsonSchemaNode? schema)
    {
        if (schema is null)
            return [];

        var upToCursor = document[..Math.Clamp(position, 0, document.Length)];
        var lines = upToCursor.Split('\n');
        var ancestry = BuildAncestry(lines.AsSpan()[..^1]);

        var (cursorIndent, cursorRest) = YamlLines.Parse(lines[^1]);
        var isArrayLine = YamlLines.IsArrayLine(cursorRest);
        YamlLines.PopFor(ancestry, cursorIndent, isArrayLine);

        var scope = Walk(schema, ancestry);
        if (isArrayLine)
        {
            scope = scope?.Items;
            cursorRest = YamlLines.StripDash(cursorRest);
        }

        if (scope is null)
            return [];

        var colon = cursorRest.IndexOf(':');
        return colon >= 0
            ? SuggestValues(scope, cursorRest[..colon].Trim(), cursorRest[(colon + 1)..].TrimStart())
            : SuggestKeys(scope, cursorRest.TrimStart());
    }

    private static IReadOnlyList<Suggestion> SuggestKeys(JsonSchemaNode scope, string prefix) =>
        [.. scope.Properties
            .Where(p => p.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => scope.Required.Contains(p.Key))
            .ThenBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new Suggestion(p.Key, p.Value.Type, scope.Required.Contains(p.Key), p.Value.Description))];

    private static IReadOnlyList<Suggestion> SuggestValues(JsonSchemaNode scope, string key, string prefix)
    {
        if (!scope.Properties.TryGetValue(key, out var property) || property.Enum is not { } values)
            return [];

        return [.. values
            .Where(v => v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(v => v, StringComparer.Ordinal)
            .Select(v => new Suggestion(v, "string", Required: false, Description: null))];
    }

    // ── Indentation-based path resolution (rules shared with YamlOutline via YamlLines) ─────

    private static List<Frame> BuildAncestry(ReadOnlySpan<string> priorLines)
    {
        var stack = new List<Frame>();

        foreach (var raw in priorLines)
        {
            var (indent, content) = YamlLines.Parse(raw);
            if (content.Length == 0 || content.StartsWith('#'))
                continue;

            var isArray = YamlLines.IsArrayLine(content);
            YamlLines.PopFor(stack, indent, isArray);

            if (isArray)
            {
                stack.Add(new Frame(indent, null, IsArrayItem: true));
                if (YamlLines.TryReadKey(YamlLines.StripDash(content), out var itemKey))
                    stack.Add(new Frame(indent + 2, itemKey, IsArrayItem: false));
            }
            else if (YamlLines.TryReadKey(content, out var key))
            {
                stack.Add(new Frame(indent, key, IsArrayItem: false));
            }
            // Anything else — a plain scalar, a flow-style {..}/[..], a "---" document separator — is
            // structure this engine does not model. It carries no path information, so it is skipped
            // rather than guessed at; the ancestry above it stays exactly as good as it already was.
        }

        return stack;
    }

    private static JsonSchemaNode? Walk(JsonSchemaNode schema, List<Frame> ancestry)
    {
        JsonSchemaNode? current = schema;
        foreach (var frame in ancestry)
        {
            if (current is null)
                return null;

            current = frame.IsArrayItem
                ? current.Items
                : frame.Key is { } key ? current.Properties.GetValueOrDefault(key) : current;
        }

        return current;
    }
}
