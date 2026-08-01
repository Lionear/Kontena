namespace Kontena.Plugins.ManifestStudio.Schemas;

/// <summary>A line-range replacement: lines <c>[StartLine, EndLine)</c> become <see cref="NewLines"/>
/// (empty for a pure deletion). Line-based rather than offset-based so the pure engine below never
/// needs to know how its caller turns this into an actual document edit.</summary>
public sealed record TextEdit(int StartLine, int EndLine, IReadOnlyList<string> NewLines);

public sealed record QuickFix(string Title, TextEdit Edit);

/// <summary>
/// Quick fixes are text edits, never rewrites (Plan §5): everything outside the edited lines —
/// comments, ordering, unrelated fields — stays exactly as written.
/// <para>
/// Only two kinds are offered, deliberately: removing a field the schema does not recognise, and
/// swapping a deprecated <c>apiVersion</c> for the one the cluster actually serves. A missing required
/// field is not offered — a schema type alone is not enough to know it needs <c>selector: {}</c>
/// rather than <c>selector:</c> with nested keys, and a wrong guess there is worse than the field
/// simply staying absent. An apiVersion migration that shifts fields around is not offered either
/// (there is no such migration known yet to withhold correctly) — "not offered" beats "half migrated"
/// (Plan §5) either way.
/// </para>
/// </summary>
public static class QuickFixEngine
{
    public static IReadOnlyList<QuickFix> Suggest(Diagnostic diagnostic, string document) => diagnostic.Kind switch
    {
        DiagnosticKind.UnknownField => RemoveField(diagnostic, document),
        DiagnosticKind.DeprecatedApiVersion => UpdateApiVersion(diagnostic, document),
        _ => [],
    };

    private static IReadOnlyList<QuickFix> RemoveField(Diagnostic diagnostic, string document)
    {
        var outline = YamlOutline.Parse(document);
        if (FindNodeAtLine(outline, diagnostic.Line) is not { } node)
            return [];

        var lastLine = LastLineOf(node);
        return [new QuickFix($"Remove '{diagnostic.FieldName}'", new TextEdit(node.Line, lastLine + 1, []))];
    }

    private static IReadOnlyList<QuickFix> UpdateApiVersion(Diagnostic diagnostic, string document)
    {
        if (diagnostic.SuggestedVersion is not { } suggested)
            return [];

        var outline = YamlOutline.Parse(document);
        var apiVersionNode = outline.Children.FirstOrDefault(c => c.Key == "apiVersion");
        if (apiVersionNode?.InlineValue is not { } current)
            return [];

        var (group, _) = ManifestDiagnostics.SplitApiVersion(current);
        var newValue = group.Length == 0 ? suggested : $"{group}/{suggested}";

        var lines = document.Split('\n');
        var line = lines[apiVersionNode.Line];
        var indent = line[..(line.Length - line.TrimStart().Length)];

        return [new QuickFix(
            $"Update apiVersion to {newValue}",
            new TextEdit(apiVersionNode.Line, apiVersionNode.Line + 1, [$"{indent}apiVersion: {newValue}"]))];
    }

    private static YamlOutline? FindNodeAtLine(YamlOutline node, int line)
    {
        foreach (var child in node.Children)
        {
            if (child.Line == line)
                return child;

            if (FindNodeAtLine(child, line) is { } found)
                return found;
        }

        return null;
    }

    private static int LastLineOf(YamlOutline node)
    {
        var last = node.Line;
        foreach (var child in node.Children)
            last = Math.Max(last, LastLineOf(child));

        return last;
    }
}
