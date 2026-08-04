using Kontena.Plugins.ManifestStudio.Kustomize;
using Kontena.Plugins.ManifestStudio.Workspace;

namespace Kontena.Plugins.ManifestStudio.Tests.Kustomize;

public sealed class OverlayDiscoveryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("manifest-studio-overlay-discovery-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Path(params string[] parts) => parts.Aggregate(_root, System.IO.Path.Combine);

    [Fact]
    public void No_kustomization_files_means_no_overlays()
    {
        File.WriteAllText(Path("deployment.yaml"), "kind: Deployment\n");

        Assert.Empty(OverlayDiscovery.Discover(ManifestWorkspace.Open(_root).Root));
    }

    [Fact]
    public void A_base_and_every_overlay_are_all_found_regardless_of_directory_name()
    {
        Directory.CreateDirectory(Path("infra", "root"));
        Directory.CreateDirectory(Path("infra", "prod"));
        Directory.CreateDirectory(Path("infra", "staging"));
        File.WriteAllText(Path("infra", "root", "kustomization.yaml"), "resources:\n  - deployment.yaml\n");
        File.WriteAllText(Path("infra", "prod", "kustomization.yaml"), "resources:\n  - ../root\n");
        File.WriteAllText(Path("infra", "staging", "kustomization.yml"), "resources:\n  - ../root\n");

        var overlays = OverlayDiscovery.Discover(ManifestWorkspace.Open(_root).Root);

        Assert.Equal(
            [Path("infra", "prod"), Path("infra", "root"), Path("infra", "staging")],
            overlays.Select(o => o.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Each_overlays_patches_are_attached_to_it()
    {
        Directory.CreateDirectory(Path("prod"));
        File.WriteAllText(Path("prod", "kustomization.yaml"), """
            resources:
              - ../base
            patches:
              - path: replicas.yaml
                target:
                  kind: Deployment
                  name: web
            """);

        var overlay = Assert.Single(OverlayDiscovery.Discover(ManifestWorkspace.Open(_root).Root));

        var patch = Assert.Single(overlay.Patches);
        Assert.Equal("Deployment", patch.TargetKind);
    }
}
