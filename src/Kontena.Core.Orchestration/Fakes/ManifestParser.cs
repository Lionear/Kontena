using System.Globalization;

namespace Kontena.Core.Orchestration.Fakes;

/// <summary>
/// A deliberately small YAML reader for the fake's apply flow: enough to pull kind, name,
/// namespace, replicas, schedule, service type/ports, labels/selector and containers out of a
/// manifest, and no more.
/// <para>
/// It is not a YAML implementation and is not meant to become one — the real Kubernetes adapter
/// (KON-68) deserializes with the official client. This exists so the declarative UI can be built
/// and demoed against realistic input before that adapter lands.
/// </para>
/// </summary>
internal static class ManifestParser
{
    private sealed record Line(int Indent, string Text, bool IsItem);

    /// <summary>Split a bundle into its <c>---</c>-separated documents and parse each one.</summary>
    public static IReadOnlyList<ManifestDoc> ParseBundle(string yaml)
    {
        var docs = new List<ManifestDoc>();
        foreach (var raw in SplitDocuments(yaml))
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            docs.Add(ParseDocument(raw));
        }

        return docs;
    }

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

    private static ManifestDoc ParseDocument(string raw)
    {
        var lines = Tokenize(raw);

        var kind = Scalar(lines, "kind", 0);
        if (string.IsNullOrEmpty(kind))
            return Invalid(raw, "missing 'kind'");

        var apiVersion = Scalar(lines, "apiVersion", 0);
        if (string.IsNullOrEmpty(apiVersion))
            return Invalid(raw, "missing 'apiVersion'");

        var metadata = Block(lines, "metadata", 0);
        var name = metadata is null ? null : Scalar(metadata, "name", metadata[0].Indent);
        if (string.IsNullOrEmpty(name))
            return Invalid(raw, "missing 'metadata.name'");

        var ns = metadata is null ? null : Scalar(metadata, "namespace", metadata[0].Indent);
        var labels = metadata is null ? [] : Map(metadata, "labels", metadata[0].Indent);

        var spec = Block(lines, "spec", 0);
        if (spec is null)
        {
            return new ManifestDoc
            {
                ApiVersion = apiVersion, Kind = kind, Name = name, Namespace = ns, Labels = labels,
            };
        }

        // A Service selects pods directly; a workload nests it one deeper under matchLabels.
        var baseIndent = spec[0].Indent;
        var selectorBlock = Block(spec, "selector", baseIndent);
        var selector = selectorBlock is not null && IndexOf(selectorBlock, "matchLabels", selectorBlock[0].Indent) >= 0
            ? Map(selectorBlock, "matchLabels", selectorBlock[0].Indent)
            : Map(spec, "selector", baseIndent);

        return new ManifestDoc
        {
            ApiVersion = apiVersion,
            Kind = kind,
            Name = name,
            Namespace = ns,
            Labels = labels,
            Replicas = Int(spec, "replicas", baseIndent),
            Schedule = Scalar(spec, "schedule", baseIndent),
            ServiceType = Scalar(spec, "type", baseIndent),
            ClusterIp = Scalar(spec, "clusterIP", baseIndent),
            NodeName = Scalar(spec, "nodeName", baseIndent),
            Selector = selector,
            Ports = ReadPorts(spec),
            Containers = ReadContainers(spec),
        };
    }

    private static ManifestDoc Invalid(string raw, string reason) => new()
    {
        Kind = "Unknown", Name = "?", Raw = raw.Trim(), Error = reason,
    };

    // ── Tokenizing ───────────────────────────────────────────────────────────

    /// <summary>
    /// Drop blanks and comments, and normalize list items: <c>- name: x</c> becomes a plain key at
    /// the item's own indent, flagged so entry boundaries stay visible.
    /// </summary>
    private static List<Line> Tokenize(string raw)
    {
        var lines = new List<Line>();
        foreach (var source in raw.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = source.TrimEnd();
            if (trimmed.Length == 0 || trimmed.TrimStart().StartsWith('#'))
                continue;

            var indent = trimmed.Length - trimmed.TrimStart().Length;
            var text = trimmed.TrimStart();

            if (text.StartsWith("- ", StringComparison.Ordinal))
                lines.Add(new Line(indent + 2, text[2..].Trim(), IsItem: true));
            else if (text == "-")
                lines.Add(new Line(indent + 2, string.Empty, IsItem: true));
            else
                lines.Add(new Line(indent, text, IsItem: false));
        }

        return lines;
    }

    // ── Lookups ──────────────────────────────────────────────────────────────

    private static int IndexOf(List<Line> lines, string key, int indent)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Indent == indent && IsKey(lines[i].Text, key))
                return i;
        }

        return -1;
    }

    private static bool IsKey(string text, string key) =>
        text.Length > key.Length && text[key.Length] == ':' && text.StartsWith(key, StringComparison.Ordinal);

    /// <summary>The value on a <c>key: value</c> line, or null when the key is absent or a block header.</summary>
    private static string? Scalar(List<Line> lines, string key, int indent)
    {
        var i = IndexOf(lines, key, indent);
        if (i < 0)
            return null;

        var value = Unquote(lines[i].Text[(key.Length + 1)..].Trim());
        return value.Length == 0 ? null : value;
    }

    private static int? Int(List<Line> lines, string key, int indent) =>
        int.TryParse(Scalar(lines, key, indent), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;

    /// <summary>The lines nested under <paramref name="key"/>, or null when there are none.</summary>
    private static List<Line>? Block(List<Line> lines, string key, int indent)
    {
        var i = IndexOf(lines, key, indent);
        if (i < 0)
            return null;

        var block = new List<Line>();
        for (var j = i + 1; j < lines.Count && lines[j].Indent > indent; j++)
            block.Add(lines[j]);

        return block.Count == 0 ? null : block;
    }

    /// <summary>A string map, written either as a nested block or as an inline <c>{a: b}</c> flow map.</summary>
    private static Dictionary<string, string> Map(List<Line> lines, string key, int indent)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        var inline = Scalar(lines, key, indent);
        if (inline is not null && inline.StartsWith('{') && inline.EndsWith('}'))
        {
            foreach (var pair in inline[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = pair.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0)
                    map[pair[..colon].Trim()] = Unquote(pair[(colon + 1)..].Trim());
            }

            return map;
        }

        var block = Block(lines, key, indent);
        if (block is null)
            return map;

        foreach (var line in block.Where(l => l.Indent == block[0].Indent))
        {
            var colon = line.Text.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
                map[line.Text[..colon].Trim()] = Unquote(line.Text[(colon + 1)..].Trim());
        }

        return map;
    }

    /// <summary>
    /// Find a list anywhere in the spec — containers sit under <c>spec.containers</c> for a Pod but
    /// under <c>spec.template.spec.containers</c> for a Deployment, and deeper still for a CronJob.
    /// </summary>
    private static List<List<Line>> Entries(List<Line> spec, string key)
    {
        var entries = new List<List<Line>>();

        for (var i = 0; i < spec.Count; i++)
        {
            if (!IsKey(spec[i].Text, key))
                continue;

            var indent = spec[i].Indent;
            List<Line>? current = null;
            for (var j = i + 1; j < spec.Count && spec[j].Indent > indent; j++)
            {
                if (spec[j].IsItem)
                {
                    current = [];
                    entries.Add(current);
                }

                current?.Add(spec[j]);
            }

            if (entries.Count > 0)
                break;
        }

        return entries;
    }

    private static List<ManifestContainer> ReadContainers(List<Line> spec)
    {
        var containers = new List<ManifestContainer>();
        foreach (var entry in Entries(spec, "containers"))
        {
            var indent = entry[0].Indent;
            var image = Scalar(entry, "image", indent);
            if (image is null)
                continue;

            containers.Add(new ManifestContainer(Scalar(entry, "name", indent) ?? "container", image));
        }

        return containers;
    }

    private static List<ManifestPort> ReadPorts(List<Line> spec)
    {
        var ports = new List<ManifestPort>();
        foreach (var entry in Entries(spec, "ports"))
        {
            var indent = entry[0].Indent;
            if (Int(entry, "port", indent) is not { } port)
                continue;

            ports.Add(new ManifestPort(
                Scalar(entry, "name", indent) ?? "port",
                port,
                Int(entry, "targetPort", indent) ?? port,
                Scalar(entry, "protocol", indent) ?? "TCP"));
        }

        return ports;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            return value[1..^1];

        // Strip a trailing inline comment on unquoted scalars.
        var hash = value.IndexOf(" #", StringComparison.Ordinal);
        return hash < 0 ? value : value[..hash].TrimEnd();
    }
}
