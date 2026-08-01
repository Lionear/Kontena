namespace Kontena.Plugins.ManifestStudio.Kustomize;

public enum LineDiffKind { Same, Changed, Added, Removed }

/// <summary>One aligned pair of lines. For <see cref="LineDiffKind.Added"/> there is no base line
/// (<c>BaseLine</c> is -1); for <see cref="LineDiffKind.Removed"/> there is no overlay line.</summary>
public sealed record LineDiffEntry(LineDiffKind Kind, int BaseLine, int OverlayLine, string? BaseText, string? OverlayText);

/// <summary>
/// A textbook LCS line diff (the same idea <c>diff</c>/<c>git diff</c> use). Needed because a naive
/// index-by-index comparison misaligns everything after the first inserted or removed line — a
/// generator adding one label would then make every following line read as "changed".
/// </summary>
public static class LineDiff
{
    public static IReadOnlyList<LineDiffEntry> Compare(IReadOnlyList<string> baseLines, IReadOnlyList<string> overlayLines)
    {
        var n = baseLines.Count;
        var m = overlayLines.Count;
        var lengths = new int[n + 1, m + 1];

        for (var i = n - 1; i >= 0; i--)
        for (var j = m - 1; j >= 0; j--)
            lengths[i, j] = baseLines[i] == overlayLines[j]
                ? lengths[i + 1, j + 1] + 1
                : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);

        var result = new List<LineDiffEntry>();
        var bi = 0;
        var oi = 0;

        while (bi < n && oi < m)
        {
            if (baseLines[bi] == overlayLines[oi])
            {
                result.Add(new LineDiffEntry(LineDiffKind.Same, bi, oi, baseLines[bi], overlayLines[oi]));
                bi++;
                oi++;
            }
            else if (lengths[bi + 1, oi] >= lengths[bi, oi + 1])
            {
                result.Add(new LineDiffEntry(LineDiffKind.Removed, bi, -1, baseLines[bi], null));
                bi++;
            }
            else
            {
                result.Add(new LineDiffEntry(LineDiffKind.Added, -1, oi, null, overlayLines[oi]));
                oi++;
            }
        }

        while (bi < n)
        {
            result.Add(new LineDiffEntry(LineDiffKind.Removed, bi, -1, baseLines[bi], null));
            bi++;
        }

        while (oi < m)
        {
            result.Add(new LineDiffEntry(LineDiffKind.Added, -1, oi, null, overlayLines[oi]));
            oi++;
        }

        return CollapseReplacements(result);
    }

    /// <summary>A removal immediately followed by an addition at the same position reads better as one
    /// "changed" line than as two — that pairing is exactly what "attribute the cause" needs.</summary>
    private static List<LineDiffEntry> CollapseReplacements(List<LineDiffEntry> entries)
    {
        var result = new List<LineDiffEntry>();

        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Kind == LineDiffKind.Removed
                && i + 1 < entries.Count && entries[i + 1].Kind == LineDiffKind.Added)
            {
                result.Add(new LineDiffEntry(
                    LineDiffKind.Changed, entries[i].BaseLine, entries[i + 1].OverlayLine,
                    entries[i].BaseText, entries[i + 1].OverlayText));
                i++;
            }
            else
            {
                result.Add(entries[i]);
            }
        }

        return result;
    }
}
