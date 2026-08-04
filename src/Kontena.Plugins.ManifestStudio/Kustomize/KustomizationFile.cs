using Kontena.Plugins.ManifestStudio.Schemas;

namespace Kontena.Plugins.ManifestStudio.Kustomize;

/// <summary>One entry from a kustomization.yaml's <c>patches:</c> list. <c>patchesStrategicMerge</c>
/// entries are not modelled here — they carry no declared target selector (the patch file's own
/// apiVersion/kind/metadata.name IS its target), so there is nothing to check them against.</summary>
public sealed record PatchDeclaration(string Path, string? TargetKind, string? TargetName);

/// <summary>The transforms <see cref="OverlayDiff"/> (KON-293 part 2) attributes a line change to.
/// Only the global transforms Plan §6 names by name; anything else stays <c>Unexplained</c> rather
/// than guessed at.</summary>
public sealed record KustomizationDirectives(
    string? NamePrefix,
    string? NameSuffix,
    IReadOnlyDictionary<string, string> CommonLabels,
    IReadOnlyDictionary<string, string> CommonAnnotations,
    IReadOnlyList<PatchDeclaration> Patches);

/// <summary>
/// Reads the handful of kustomization.yaml fields this epic needs, via <see cref="YamlOutline"/> —
/// kustomization.yaml is YAML like any manifest, so the same lenient, indentation-based reader applies
/// rather than a second parser.
/// </summary>
public static class KustomizationFile
{
    public static IReadOnlyList<PatchDeclaration> ParsePatches(string yaml) =>
        ParsePatches(YamlOutline.Parse(yaml));

    public static KustomizationDirectives ParseDirectives(string yaml)
    {
        var root = YamlOutline.Parse(yaml);

        return new KustomizationDirectives(
            root.Children.FirstOrDefault(c => c.Key == "namePrefix")?.InlineValue,
            root.Children.FirstOrDefault(c => c.Key == "nameSuffix")?.InlineValue,
            ParseMap(root, "commonLabels"),
            ParseMap(root, "commonAnnotations"),
            ParsePatches(root));
    }

    private static List<PatchDeclaration> ParsePatches(YamlOutline root)
    {
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

    private static Dictionary<string, string> ParseMap(YamlOutline root, string key)
    {
        var node = root.Children.FirstOrDefault(c => c.Key == key);
        if (node is null)
            return new Dictionary<string, string>();

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var child in node.Children)
            if (child.Key is { } k && child.InlineValue is { } v)
                map[k] = v;

        return map;
    }
}
