using System.Text;

namespace Kontena.Core.Orchestration;

/// <summary>
/// Line-based unified diff between a live manifest and a desired one — what the dry-run plan shows
/// per resource. It lives next to the OAL rather than inside a fake because every adapter that
/// renders a client-side diff (the real Kubernetes one included) needs the same output shape:
/// context lines prefixed with a space, removals with <c>-</c>, additions with <c>+</c>, and
/// <c>…</c> where unchanged runs were collapsed.
/// </summary>
public static class ManifestDiff
{
    /// <summary>Above this many lines the LCS table gets expensive; fall back to a whole-file diff.</summary>
    private const int MaxLines = 1000;

    /// <summary>
    /// Diff <paramref name="live"/> against <paramref name="desired"/>, keeping
    /// <paramref name="context"/> unchanged lines around each change. Returns an empty string when
    /// the two are identical.
    /// </summary>
    public static string Compute(string live, string desired, int context = 3)
    {
        var a = SplitLines(live);
        var b = SplitLines(desired);

        if (a.Length == 0 && b.Length == 0)
            return string.Empty;

        var ops = a.Length > MaxLines || b.Length > MaxLines ? WholeFile(a, b) : Align(a, b);
        return Render(ops, context);
    }

    private static string[] SplitLines(string text) =>
        string.IsNullOrEmpty(text)
            ? []
            : text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');

    /// <summary>Longest-common-subsequence alignment — the classic diff core.</summary>
    private static List<(char Kind, string Text)> Align(string[] a, string[] b)
    {
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
            {
                lcs[i, j] = string.Equals(a[i], b[j], StringComparison.Ordinal)
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var ops = new List<(char, string)>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (string.Equals(a[x], b[y], StringComparison.Ordinal))
            {
                ops.Add((' ', a[x]));
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                ops.Add(('-', a[x++]));
            }
            else
            {
                ops.Add(('+', b[y++]));
            }
        }

        while (x < a.Length)
            ops.Add(('-', a[x++]));
        while (y < b.Length)
            ops.Add(('+', b[y++]));

        return ops;
    }

    private static List<(char Kind, string Text)> WholeFile(string[] a, string[] b)
    {
        var ops = new List<(char, string)>(a.Length + b.Length);
        foreach (var line in a)
            ops.Add(('-', line));
        foreach (var line in b)
            ops.Add(('+', line));
        return ops;
    }

    private static string Render(List<(char Kind, string Text)> ops, int context)
    {
        if (ops.TrueForAll(o => o.Kind == ' '))
            return string.Empty;

        // Keep every changed line plus `context` lines on either side; collapse the rest.
        var keep = new bool[ops.Count];
        for (var i = 0; i < ops.Count; i++)
        {
            if (ops[i].Kind == ' ')
                continue;

            var from = Math.Max(0, i - context);
            var to = Math.Min(ops.Count - 1, i + context);
            for (var j = from; j <= to; j++)
                keep[j] = true;
        }

        var sb = new StringBuilder();
        var collapsed = false;
        for (var i = 0; i < ops.Count; i++)
        {
            if (!keep[i])
            {
                collapsed = true;
                continue;
            }

            if (collapsed)
            {
                sb.Append("…\n");
                collapsed = false;
            }

            sb.Append(ops[i].Kind).Append(ops[i].Text).Append('\n');
        }

        return sb.ToString().TrimEnd('\n');
    }
}
