using Kontena.Plugins.ManifestStudio.Schemas;

namespace Kontena.Plugins.ManifestStudio.Kustomize;

public enum DiffCause { NamePrefix, NameSuffix, CommonLabel, CommonAnnotation, Patch, Unexplained }

/// <summary>One changed line plus why it changed. <see cref="Cause"/> is <see cref="DiffCause.Unexplained"/>
/// whenever this cannot say for certain — Plan §6 wants the real reason surfaced, not a guess dressed
/// up as one.</summary>
public sealed record AttributedDiff(LineDiffEntry Line, DiffCause Cause, string? Detail);

/// <summary>
/// Answers the question the rendered YAML alone cannot (Plan §6): for each line that differs between a
/// base and one of its overlays, which of the overlay's own declared transforms caused it.
/// <para>
/// Resources are paired by position — the Nth document in the base render against the Nth in the
/// overlay render. Kustomize does not reorder or drop what it started with; a generator can still add
/// resources the base never had, which is exactly why pairing stops at whichever render is shorter
/// rather than guessing which extra resource matches which.
/// </para>
/// <para>
/// Every cause here is verified, never inferred from proximity: a name change must equal
/// <c>prefix + base + suffix</c> exactly, a label/annotation line must equal the declared key and
/// value exactly, and a patch is only credited when its own file, on disk, actually contains the
/// resulting line. A patch whose target matches but whose content does not explain the line stays
/// <see cref="DiffCause.Unexplained"/> — the same "half migrated is worse than the warning standing"
/// reasoning QuickFixEngine (KON-292) already applies, applied here to attribution instead of edits.
/// </para>
/// </summary>
public static class OverlayDiff
{
    public static IReadOnlyList<AttributedDiff> Compare(
        string baseYaml, string overlayYaml, KustomizationDirectives directives, string overlayDirectory)
    {
        var baseDocs = ManifestDiagnostics.SplitDocuments(baseYaml).Select(d => d.Text).ToArray();
        var overlayDocs = ManifestDiagnostics.SplitDocuments(overlayYaml).Select(d => d.Text).ToArray();

        var results = new List<AttributedDiff>();
        for (var i = 0; i < Math.Min(baseDocs.Length, overlayDocs.Length); i++)
            results.AddRange(CompareDocument(baseDocs[i], overlayDocs[i], directives, overlayDirectory));

        return results;
    }

    private static IReadOnlyList<AttributedDiff> CompareDocument(
        string baseDoc, string overlayDoc, KustomizationDirectives directives, string overlayDirectory)
    {
        // ManifestDiagnostics.SplitDocuments keeps a trailing blank line for the last document in a
        // bundle but not for one that precedes a "---" — a difference that means nothing structurally
        // (YamlOutline already ignores blank lines) but would otherwise show up as a spurious removed
        // line here, where the diff is over raw text.
        var diff = LineDiff.Compare(
            baseDoc.TrimEnd('\n', '\r').Split('\n'), overlayDoc.TrimEnd('\n', '\r').Split('\n'));

        var outline = YamlOutline.Parse(overlayDoc);
        var kind = outline.Children.FirstOrDefault(c => c.Key == "kind")?.InlineValue;
        var name = outline.Children.FirstOrDefault(c => c.Key == "metadata") is { } metadata
            ? metadata.Children.FirstOrDefault(c => c.Key == "name")?.InlineValue
            : null;

        var relevantPatches = directives.Patches
            .Where(p => (p.TargetKind is null || p.TargetKind == kind) && (p.TargetName is null || p.TargetName == name))
            .ToArray();

        return [.. diff
            .Where(e => e.Kind != LineDiffKind.Same)
            .Select(e =>
            {
                var (cause, detail) = Attribute(e, directives, relevantPatches, overlayDirectory);
                return new AttributedDiff(e, cause, detail);
            })];
    }

    private static (DiffCause Cause, string? Detail) Attribute(
        LineDiffEntry entry, KustomizationDirectives directives,
        IReadOnlyList<PatchDeclaration> relevantPatches, string overlayDirectory)
    {
        if (entry.OverlayText is not { } overlayText)
            return (DiffCause.Unexplained, null);

        var trimmed = overlayText.Trim();

        if (entry is { Kind: LineDiffKind.Changed, BaseText: { } baseText }
            && AttributeNameChange(baseText.Trim(), trimmed, directives) is { } nameCause)
            return nameCause;

        foreach (var (key, value) in directives.CommonLabels)
            if (trimmed == $"{key}: {value}")
                return (DiffCause.CommonLabel, key);

        foreach (var (key, value) in directives.CommonAnnotations)
            if (trimmed == $"{key}: {value}")
                return (DiffCause.CommonAnnotation, key);

        foreach (var patch in relevantPatches)
        {
            var patchPath = Path.Combine(overlayDirectory, patch.Path);
            if (File.Exists(patchPath) && File.ReadAllLines(patchPath).Any(l => l.Trim() == trimmed))
                return (DiffCause.Patch, patch.Path);
        }

        return (DiffCause.Unexplained, null);
    }

    private static (DiffCause, string?)? AttributeNameChange(
        string baseLine, string overlayLine, KustomizationDirectives directives)
    {
        if (!baseLine.StartsWith("name:", StringComparison.Ordinal) || !overlayLine.StartsWith("name:", StringComparison.Ordinal))
            return null;

        var baseName = baseLine["name:".Length..].Trim();
        var overlayName = overlayLine["name:".Length..].Trim();
        var prefix = directives.NamePrefix ?? string.Empty;
        var suffix = directives.NameSuffix ?? string.Empty;

        if ((prefix.Length == 0 && suffix.Length == 0) || overlayName != prefix + baseName + suffix)
            return null;

        return prefix.Length switch
        {
            > 0 when suffix.Length > 0 => (DiffCause.NamePrefix, $"{prefix}…{suffix}"),
            > 0 => (DiffCause.NamePrefix, prefix),
            _ => (DiffCause.NameSuffix, suffix),
        };
    }
}
