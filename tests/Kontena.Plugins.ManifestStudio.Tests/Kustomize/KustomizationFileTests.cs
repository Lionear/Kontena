using Kontena.Plugins.ManifestStudio.Kustomize;

namespace Kontena.Plugins.ManifestStudio.Tests.Kustomize;

public sealed class KustomizationFileTests
{
    [Fact]
    public void No_patches_key_at_all_yields_nothing()
    {
        Assert.Empty(KustomizationFile.ParsePatches("resources:\n  - deployment.yaml\n"));
    }

    [Fact]
    public void A_patch_with_a_target_is_read()
    {
        var patches = KustomizationFile.ParsePatches("""
            patches:
              - path: replicas.yaml
                target:
                  kind: Deployment
                  name: web
            """);

        var patch = Assert.Single(patches);
        Assert.Equal("replicas.yaml", patch.Path);
        Assert.Equal("Deployment", patch.TargetKind);
        Assert.Equal("web", patch.TargetName);
    }

    [Fact]
    public void A_patch_without_a_target_has_null_kind_and_name()
    {
        var patch = Assert.Single(KustomizationFile.ParsePatches("""
            patches:
              - path: replicas.yaml
            """));

        Assert.Null(patch.TargetKind);
        Assert.Null(patch.TargetName);
    }

    [Fact]
    public void Multiple_patches_are_all_read_in_order()
    {
        var patches = KustomizationFile.ParsePatches("""
            patches:
              - path: a.yaml
                target:
                  kind: Deployment
                  name: web
              - path: b.yaml
                target:
                  kind: Service
                  name: web
            """);

        Assert.Equal(["a.yaml", "b.yaml"], patches.Select(p => p.Path));
    }
}
