using System.Text;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Core.Orchestration;

/// <summary>
/// Puts an edited set of keys back into a ConfigMap's or Secret's own manifest (KON-422).
/// <para>
/// <b>Why this is text surgery and not a parse.</b> There is no YAML library in this repository and
/// this is not the place to introduce one: what has to survive the trip is everything the editor
/// never touched — annotations, ownerReferences, a <c>type:</c>, the fields a controller wrote —
/// and the surest way to preserve those is to not re-serialise them. So the manifest the cluster
/// gave us is kept verbatim apart from one block, which is replaced whole.
/// </para>
/// <para>
/// <b>What it refuses.</b> Anything it cannot be certain about: a bundle of several documents, and
/// a manifest with no <c>kind:</c> at the top level — the fetch failing produces a comment, and a
/// comment is not an object to edit. Refusing is a null, not an exception, because the caller has a
/// status line for exactly this.
/// </para>
/// </summary>
public static class ConfigManifest
{
    /// <summary>
    /// The manifest with its data block replaced by <paramref name="data"/> — key to base64, the
    /// form the API stores whichever kind this is. Null when the input is not a single editable
    /// document.
    /// </summary>
    public static string? WithData(string manifest, IReadOnlyDictionary<string, string> data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (string.IsNullOrWhiteSpace(manifest))
            return null;

        var text = manifest.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = text.Split('\n');

        // A bundle is somebody else's document as much as ours, and picking one out of it by
        // guessing which is a way to write the wrong object. A leading marker is not a bundle
        // though — that is one document that says so.
        if (SeparatesDocuments(lines))
            return null;

        if (!lines.Any(l => IsTopLevelKey(l, "kind")))
            return null;

        var kept = new List<string>();
        var insertAt = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (!IsTopLevelKey(line, "data") && !IsTopLevelKey(line, "stringData"))
            {
                kept.Add(line);
                continue;
            }

            // Where data stood is where it goes back, so a manifest read by a human keeps its
            // shape. stringData leaves without a replacement: every key is rendered into data
            // below, and a leftover stringData entry would quietly win over the one beside it.
            if (insertAt < 0 && IsTopLevelKey(line, "data"))
                insertAt = kept.Count;

            // The block is the header's nested lines. A blank line inside it is still inside it;
            // the next line at column zero is what ends it.
            for (var j = i + 1; j < lines.Length; j++)
            {
                if (lines[j].Trim().Length > 0 && !char.IsWhiteSpace(lines[j][0]))
                    break;

                i = j;
            }
        }

        var block = Render(data);

        if (insertAt < 0)
        {
            // No data block to replace — a Secret whose keys were all removed at some point, or one
            // written without any. Appended, which is where a top-level field can always go.
            while (kept.Count > 0 && kept[^1].Trim().Length == 0)
                kept.RemoveAt(kept.Count - 1);

            kept.AddRange(block);
        }
        else
        {
            kept.InsertRange(insertAt, block);
        }

        return string.Join('\n', kept).TrimEnd('\n') + "\n";
    }

    /// <summary>
    /// The rows an editor holds, as the data map. Text is encoded; a key whose value was never
    /// decoded — a certificate — is passed through in the form it arrived in, untouched.
    /// </summary>
    public static IReadOnlyDictionary<string, string> DataOf(IEnumerable<ConfigEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
            data[entry.Key] = ConfigBytes.Base64Of(entry);

        return data;
    }

    private static List<string> Render(IReadOnlyDictionary<string, string> data)
    {
        var block = new List<string> { "data:" };

        // Sorted, because the apiserver stores a map and hands it back in its own order: leaving it
        // in editor order would make a re-read look like a change on every apply.
        foreach (var (key, base64) in data.OrderBy(d => d.Key, StringComparer.Ordinal))
            block.Add($"  {Quote(key)}: {base64}");

        return block;
    }

    /// <summary>
    /// Kubernetes key names are already plain YAML scalars — letters, digits, <c>-</c>, <c>_</c> and
    /// <c>.</c> — so quoting is only ever reached by something the apiserver would reject anyway.
    /// It is here so that a name from a hand-edited manifest cannot break the document's syntax on
    /// its way through us.
    /// </summary>
    private static string Quote(string key) =>
        key.Length > 0 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            ? key
            : "\"" + key.Replace("\\", "\\\\", StringComparison.Ordinal)
                        .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    /// <summary>Whether a <c>---</c> appears with a document already behind it.</summary>
    private static bool SeparatesDocuments(string[] lines)
    {
        var seenContent = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed == "---")
            {
                if (seenContent)
                    return true;

                continue;
            }

            if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
                seenContent = true;
        }

        return false;
    }

    private static bool IsTopLevelKey(string line, string key) =>
        line.Length > key.Length
        && line[key.Length] == ':'
        && line.StartsWith(key, StringComparison.Ordinal);
}
