namespace Kontena.Plugins.ManifestStudio.Schemas;

/// <summary>
/// Indentation-only line classification, shared between <see cref="CompletionEngine"/> (the path up to
/// the cursor) and <see cref="YamlOutline"/> (the whole document's structure, for
/// <see cref="ManifestDiagnostics"/>). Both need the exact same answer to "does this line open, close,
/// or replace a scope" — keeping that rule in one place is the only way flush-style and indented-style
/// lists can never resolve to one path for completion and a different one for diagnostics.
/// </summary>
internal static class YamlLines
{
    public readonly record struct Frame(int Indent, string? Key, bool IsArrayItem);

    public static bool IsArrayLine(string content) =>
        content == "-" || content.StartsWith("- ", StringComparison.Ordinal);

    public static string StripDash(string content) => content.Length > 1 ? content[2..] : "";

    public static bool TryReadKey(string content, out string key)
    {
        var colon = content.IndexOf(':');
        if (colon <= 0)
        {
            key = "";
            return false;
        }

        key = content[..colon].Trim();
        return key.Length > 0;
    }

    public static (int Indent, string Content) Parse(string line)
    {
        var trimmed = line.TrimEnd('\r');
        var indent = 0;
        while (indent < trimmed.Length && trimmed[indent] == ' ')
            indent++;

        return (indent, trimmed[indent..]);
    }

    /// <summary>
    /// A mapping key at indent N closes any sibling above it, list marker or key alike. A list marker
    /// at indent N does not — YAML allows a sequence to sit flush with its own key — except a
    /// *previous item* of the same list, which it does replace.
    /// </summary>
    public static void PopFor(List<Frame> stack, int indent, bool isArrayLine)
    {
        while (stack.Count > 0 && (isArrayLine
                   ? stack[^1].Indent > indent || (stack[^1].Indent == indent && stack[^1].IsArrayItem)
                   : stack[^1].Indent >= indent))
        {
            stack.RemoveAt(stack.Count - 1);
        }
    }
}
