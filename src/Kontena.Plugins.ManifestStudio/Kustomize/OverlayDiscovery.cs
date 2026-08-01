using Kontena.Plugins.ManifestStudio.Workspace;

namespace Kontena.Plugins.ManifestStudio.Kustomize;

/// <summary>One directory in the workspace that holds a kustomization.yaml.</summary>
public sealed record Overlay(string Path, IReadOnlyList<PatchDeclaration> Patches);

/// <summary>
/// Finds every kustomization in a workspace — on the files, never on a directory naming convention
/// like "overlays/" or "base/" (Plan §6, the same rule <see cref="ManifestWorkspace"/> already applies
/// to "is this a Kustomize project at all").
/// </summary>
public static class OverlayDiscovery
{
    private static readonly string[] KustomizationNames = ["kustomization.yaml", "kustomization.yml"];

    public static IReadOnlyList<Overlay> Discover(WorkspaceFolder root)
    {
        var overlays = new List<Overlay>();
        Walk(root, overlays);
        return overlays;
    }

    private static void Walk(WorkspaceNode node, List<Overlay> overlays)
    {
        if (node is WorkspaceFile file
            && KustomizationNames.Contains(file.Name, StringComparer.OrdinalIgnoreCase))
        {
            var directory = System.IO.Path.GetDirectoryName(file.FullPath)!;
            var patches = KustomizationFile.ParsePatches(File.ReadAllText(file.FullPath));
            overlays.Add(new Overlay(directory, patches));
        }

        foreach (var child in node.Children)
            Walk(child, overlays);
    }
}
