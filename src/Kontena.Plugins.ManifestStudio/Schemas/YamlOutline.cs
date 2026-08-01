using Frame = Kontena.Plugins.ManifestStudio.Schemas.YamlLines.Frame;

namespace Kontena.Plugins.ManifestStudio.Schemas;

/// <summary>
/// The whole document's structure — every key, its line, and its children — built with the exact same
/// indentation rules <see cref="CompletionEngine"/> uses for the path up to the cursor
/// (<see cref="YamlLines"/>), so <see cref="ManifestDiagnostics"/> never disagrees with completion
/// about what a flush-style or indented-style list means.
/// <para>
/// One document only — a multi-document bundle (<c>---</c>-separated) is split by the caller first
/// (<see cref="ManifestDiagnostics"/> does this), because a document separator has no indentation of
/// its own to resolve against.
/// </para>
/// </summary>
public sealed class YamlOutline
{
    public string? Key { get; private init; }
    public int Line { get; private init; }
    public int Indent { get; private init; }
    public bool IsArrayItem { get; private init; }

    /// <summary>The text after the colon on this key's own line, or after "- " for a bare array item;
    /// null when the value is a nested block (more indented lines follow) rather than inline.</summary>
    public string? InlineValue { get; private init; }

    public List<YamlOutline> Children { get; } = [];

    public static YamlOutline Parse(string document)
    {
        var root = new YamlOutline { Line = -1, Indent = -1 };
        var frames = new List<Frame> { new(-1, null, false) };
        var nodes = new List<YamlOutline> { root };

        var lines = document.Split('\n');
        for (var lineNumber = 0; lineNumber < lines.Length; lineNumber++)
        {
            var (indent, content) = YamlLines.Parse(lines[lineNumber]);
            if (content.Length == 0 || content.StartsWith('#'))
                continue;

            var isArray = YamlLines.IsArrayLine(content);
            Pop(frames, nodes, indent, isArray);

            if (isArray)
            {
                var item = new YamlOutline { Line = lineNumber, Indent = indent, IsArrayItem = true };
                nodes[^1].Children.Add(item);
                frames.Add(new Frame(indent, null, IsArrayItem: true));
                nodes.Add(item);

                var rest = YamlLines.StripDash(content);
                if (rest.Length > 0)
                    AddKeyish(rest, indent + 2, lineNumber, frames, nodes);
            }
            else
            {
                AddKeyish(content, indent, lineNumber, frames, nodes);
            }
        }

        return root;
    }

    private static void AddKeyish(
        string content, int indent, int lineNumber, List<Frame> frames, List<YamlOutline> nodes)
    {
        if (!YamlLines.TryReadKey(content, out var key))
            return; // A bare scalar list item or similar — no key, nothing further to attach to.

        var value = content[(content.IndexOf(':') + 1)..].Trim();
        var node = new YamlOutline
        {
            Key = key, Line = lineNumber, Indent = indent, InlineValue = value.Length > 0 ? value : null,
        };

        nodes[^1].Children.Add(node);
        frames.Add(new Frame(indent, key, IsArrayItem: false));
        nodes.Add(node);
    }

    private static void Pop(List<Frame> frames, List<YamlOutline> nodes, int indent, bool isArrayLine)
    {
        var before = frames.Count;
        YamlLines.PopFor(frames, indent, isArrayLine);
        nodes.RemoveRange(nodes.Count - (before - frames.Count), before - frames.Count);
    }
}
