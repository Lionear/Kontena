using Kontena.Plugins.ManifestStudio.Schemas;

namespace Kontena.Plugins.ManifestStudio.Kustomize;

/// <summary>A declared patch whose <c>target</c> selector matched nothing in the rendered output.</summary>
public sealed record UnmatchedPatch(string Path, string? TargetKind, string? TargetName);

/// <summary>
/// Kustomize accepts a <c>patches:</c> entry whose <c>target</c> matches zero resources without a word
/// of complaint (Plan §6: "de meest voorkomende overlay-fout", and one Kustomize is silent about by
/// design) — this is the check that says so instead.
/// <para>
/// ponytail: <c>target.name</c> is matched exactly; Kustomize itself treats it as a regular expression.
/// A patch that only matches through a regex (not literally) reads as unmatched here — narrower than
/// Kustomize, never wider, so this never invents a false "matches nothing". Widen if a real overlay
/// hits it.
/// </para>
/// </summary>
public static class PatchMatchChecker
{
    public static IReadOnlyList<UnmatchedPatch> FindUnmatched(
        IReadOnlyList<PatchDeclaration> patches, string renderedYaml)
    {
        if (patches.Count == 0)
            return [];

        var rendered = ResourcesIn(renderedYaml);
        var unmatched = new List<UnmatchedPatch>();

        foreach (var patch in patches)
        {
            // Nothing declared to check against (e.g. a labelSelector target, not modelled here) —
            // unverifiable, not "matches nothing".
            if (patch.TargetKind is null && patch.TargetName is null)
                continue;

            var matched = rendered.Any(r =>
                (patch.TargetKind is null || string.Equals(r.Kind, patch.TargetKind, StringComparison.Ordinal))
                && (patch.TargetName is null || string.Equals(r.Name, patch.TargetName, StringComparison.Ordinal)));

            if (!matched)
                unmatched.Add(new UnmatchedPatch(patch.Path, patch.TargetKind, patch.TargetName));
        }

        return unmatched;
    }

    private static IReadOnlyList<(string? Kind, string? Name)> ResourcesIn(string renderedYaml) =>
        [.. ManifestDiagnostics.SplitDocuments(renderedYaml).Select(d =>
        {
            var outline = YamlOutline.Parse(d.Text);
            var kind = outline.Children.FirstOrDefault(c => c.Key == "kind")?.InlineValue;
            var name = outline.Children.FirstOrDefault(c => c.Key == "metadata") is { } metadata
                ? metadata.Children.FirstOrDefault(c => c.Key == "name")?.InlineValue
                : null;
            return (kind, name);
        })];
}
