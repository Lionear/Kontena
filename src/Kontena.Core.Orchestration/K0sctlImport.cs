using Kontena.Sdk.Orchestration.Provisioning;

namespace Kontena.Core.Orchestration;

/// <summary>
/// Reads the machines out of a k0sctl.yaml, so someone who already has one does not type it again
/// (KON-233).
/// <para>
/// Hand-parsed rather than through a YAML library, which is the standing choice in this solution:
/// nothing here takes a YAML dependency, not even the manifest editor. What is needed is one known
/// list at a known key, and a reader for exactly that is smaller than the dependency it would replace.
/// </para>
/// <para>
/// Forgiving on purpose. This is an import, not a validator — anything it cannot read it leaves out,
/// and the host table shows what came through so it can be corrected by hand. k0sctl remains the
/// authority on whether the file is really valid.
/// </para>
/// </summary>
public static class K0sctlImport
{
    /// <summary>
    /// The hosts in <paramref name="yaml"/>, in file order. Empty when there is no readable
    /// <c>hosts:</c> list — the caller says so in its own words, because "not a k0sctl.yaml" and "a
    /// k0sctl.yaml with no hosts in it" read the same from here.
    /// </summary>
    public static IReadOnlyList<RemoteClusterHost> ReadHosts(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return [];

        var lines = Joined(yaml);

        var start = lines.FindIndex(l => Key(l.Text) == "hosts");
        if (start < 0)
            return [];

        var hosts = new List<RemoteClusterHost>();
        var listIndent = lines[start].Indent;

        foreach (var item in Items(lines, start + 1, listIndent))
        {
            if (Host(item) is { } host)
                hosts.Add(host);
        }

        return hosts;
    }

    private static RemoteClusterHost? Host(List<Line> item)
    {
        var indent = item[0].Indent;
        string? address = null, user = null, keyPath = null, role = null, hostname = null;

        for (var i = 0; i < item.Count; i++)
        {
            if (item[i].Indent != indent)
                continue;

            var key = Key(item[i].Text);
            var value = Value(item[i].Text);

            switch (key)
            {
                case "role":
                    role = value;
                    break;

                case "hostname":
                    hostname = value;
                    break;

                // Not what k0sctl writes — it nests these under ssh — but a hand-edited file may put
                // them here, and reading one costs less than refusing it.
                case "address":
                    address ??= value;
                    break;
                case "user":
                    user ??= value;
                    break;
                case "keyPath":
                    keyPath ??= value;
                    break;

                case "ssh":
                    Ssh(item, i, indent, value, ref address, ref user, ref keyPath);
                    break;

                default:
                    break;
            }
        }

        // No address is nothing to import: the row would be blank in the table and blank in the spec.
        // A localhost: or winrm: host lands here too, which is honest — we cannot reach those this way.
        return string.IsNullOrEmpty(address)
            ? null
            : new RemoteClusterHost(address, Role(role))
            {
                User = Blank(user),
                KeyPath = Blank(keyPath),
                NodeName = Blank(hostname),
            };
    }

    /// <summary>The <c>ssh:</c> block, in either shape: a flow mapping on the key line, or nested keys.</summary>
    private static void Ssh(
        List<Line> item, int at, int indent, string value,
        ref string? address, ref string? user, ref string? keyPath)
    {
        if (value.StartsWith('{'))
        {
            foreach (var (key, pair) in Flow(value))
                Take(key, pair, ref address, ref user, ref keyPath);

            return;
        }

        for (var i = at + 1; i < item.Count && item[i].Indent > indent; i++)
            Take(Key(item[i].Text), Value(item[i].Text), ref address, ref user, ref keyPath);
    }

    private static void Take(
        string key, string value, ref string? address, ref string? user, ref string? keyPath)
    {
        switch (key)
        {
            case "address": address = value; break;
            case "user": user = value; break;
            case "keyPath": keyPath = value; break;
            default: break;
        }
    }

    /// <summary>
    /// k0sctl's role words, mapped onto the two roles a spec has.
    /// <para>
    /// <c>controller+worker</c> and <c>single</c> both become <see cref="ClusterHostRole.Controller"/>.
    /// That drops the fact that they also run workloads, which is real but not something
    /// <see cref="ClusterHostRole"/> can hold today — and calling a controller a worker would lose the
    /// half that decides whether the cluster has a control plane at all.
    /// </para>
    /// <para>An unknown or missing role becomes a worker, which is k0sctl's own default.</para>
    /// </summary>
    private static ClusterHostRole Role(string? role) =>
        role?.Trim().ToLowerInvariant() is "controller" or "controller+worker" or "single"
            ? ClusterHostRole.Controller
            : ClusterHostRole.Worker;

    /// <summary>The list items under a <c>key:</c> at <paramref name="listIndent"/>, one line block each.</summary>
    private static List<List<Line>> Items(List<Line> lines, int from, int listIndent)
    {
        var items = new List<List<Line>>();

        for (var i = from; i < lines.Count; i++)
        {
            var line = lines[i];

            // Back out to the level of hosts: or shallower, and the list is over.
            if (line.Indent <= listIndent)
                break;

            if (!line.Text.StartsWith('-'))
            {
                if (items.Count > 0)
                    items[^1].Add(line);

                continue;
            }

            // "- ssh:" — the dash is structure, the rest is the item's first key at its own indent.
            var rest = line.Text[1..].TrimStart();
            if (rest.Length == 0)
            {
                items.Add([]);
                continue;
            }

            items.Add([new Line(line.Indent + (line.Text.Length - rest.Length), rest)]);
        }

        return [.. items.Where(i => i.Count > 0)];
    }

    /// <summary>
    /// The file as indent/text pairs, with comments and blank lines gone and any flow mapping that ran
    /// over several lines pulled back onto one.
    /// </summary>
    private static List<Line> Joined(string yaml)
    {
        var lines = new List<Line>();

        foreach (var raw in yaml.ReplaceLineEndings("\n").Split('\n'))
        {
            var text = Uncommented(raw.Replace("\t", "    ", StringComparison.Ordinal)).TrimEnd();
            if (text.Trim().Length == 0)
                continue;

            var indent = text.Length - text.TrimStart().Length;

            // An unbalanced { means the mapping continues on the next line; keep filling this one.
            if (lines.Count > 0 && Unbalanced(lines[^1].Text))
                lines[^1] = lines[^1] with { Text = lines[^1].Text + " " + text.Trim() };
            else
                lines.Add(new Line(indent, text.Trim()));
        }

        return lines;
    }

    private static bool Unbalanced(string text)
    {
        var depth = 0;
        var quote = '\0';

        foreach (var c in text)
        {
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
            }
            else if (c is '\'' or '"')
                quote = c;
            else if (c == '{')
                depth++;
            else if (c == '}')
                depth--;
        }

        return depth > 0;
    }

    /// <summary>Drops a trailing <c>#</c> comment, leaving one inside quotes alone.</summary>
    private static string Uncommented(string text)
    {
        var quote = '\0';

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
            }
            else if (c is '\'' or '"')
                quote = c;
            else if (c == '#' && (i == 0 || char.IsWhiteSpace(text[i - 1])))
                return text[..i];
        }

        return text;
    }

    private static IEnumerable<(string Key, string Value)> Flow(string text)
    {
        var inner = text.Trim();
        inner = inner[1..(inner.EndsWith('}') ? ^1 : ^0)];

        foreach (var part in Split(inner))
        {
            var pair = part.Trim();
            var colon = pair.IndexOf(':', StringComparison.Ordinal);

            if (colon > 0)
                yield return (pair[..colon].Trim(), Unquoted(pair[(colon + 1)..].Trim()));
        }
    }

    /// <summary>Splits on commas that are not inside quotes.</summary>
    private static IEnumerable<string> Split(string text)
    {
        var quote = '\0';
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
            }
            else if (c is '\'' or '"')
                quote = c;
            else if (c == ',')
            {
                yield return text[start..i];
                start = i + 1;
            }
        }

        yield return text[start..];
    }

    private static string Key(string text)
    {
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        return colon <= 0 ? string.Empty : text[..colon].Trim();
    }

    private static string Value(string text)
    {
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? string.Empty : Unquoted(text[(colon + 1)..].Trim());
    }

    private static string Unquoted(string text) =>
        text.Length >= 2 && text[0] == text[^1] && text[0] is '\'' or '"'
            ? text[1..^1]
            : text;

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private readonly record struct Line(int Indent, string Text);
}
