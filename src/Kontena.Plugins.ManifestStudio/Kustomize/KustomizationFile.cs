using Kontena.Plugins.ManifestStudio.Schemas;

namespace Kontena.Plugins.ManifestStudio.Kustomize;

/// <summary>One entry from a kustomization.yaml's <c>patches:</c> list. <c>patchesStrategicMerge</c>
/// entries are not modelled here — they carry no declared target selector (the patch file's own
/// apiVersion/kind/metadata.name IS its target), so there is nothing to check them against.</summary>
public sealed record PatchDeclaration(string Path, string? TargetKind, string? TargetName);

/// <summary>
/// Reads the handful of kustomization.yaml fields this epic's first cut needs, via
/// <see cref="YamlOutline"/> — kustomization.yaml is YAML like any manifest, so the same lenient,
/// indentation-based reader applies rather than a second parser.
/// </summary>
public static class KustomizationFile
{
    public static IReadOnlyList<PatchDeclaration> ParsePatches(string yaml)
    {
        var root = YamlOutline.Parse(yaml);
        var patchesNode = root.Children.FirstOrDefault(c => c.Key == "patches");
        if (patchesNode is null)
            return [];

        var patches = new List<PatchDeclaration>();
        foreach (var item in patchesNode.Children.Where(c => c.IsArrayItem))
        {
            var path = item.Children.FirstOrDefault(c => c.Key == "path")?.InlineValue;
            if (path is null)
                continue;

            var target = item.Children.FirstOrDefault(c => c.Key == "target");
            var kind = target?.Children.FirstOrDefault(c => c.Key == "kind")?.InlineValue;
            var name = target?.Children.FirstOrDefault(c => c.Key == "name")?.InlineValue;
            patches.Add(new PatchDeclaration(path, kind, name));
        }

        return patches;
    }
}
