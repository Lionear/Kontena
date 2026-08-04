using Kontena.Plugins.ManifestStudio.Kustomize;

namespace Kontena.Plugins.ManifestStudio.Tests.Kustomize;

public sealed class PatchMatchCheckerTests
{
    private const string Rendered = """
        apiVersion: apps/v1
        kind: Deployment
        metadata:
          name: web
        ---
        apiVersion: v1
        kind: Service
        metadata:
          name: web
        """;

    [Fact]
    public void No_patches_means_nothing_to_report()
    {
        Assert.Empty(PatchMatchChecker.FindUnmatched([], Rendered));
    }

    [Fact]
    public void A_patch_matching_a_real_resource_is_not_flagged()
    {
        var patches = new[] { new PatchDeclaration("replicas.yaml", "Deployment", "web") };

        Assert.Empty(PatchMatchChecker.FindUnmatched(patches, Rendered));
    }

    [Fact]
    public void A_patch_targeting_a_kind_that_does_not_exist_is_flagged()
    {
        var patches = new[] { new PatchDeclaration("bogus.yaml", "StatefulSet", "web") };

        var unmatched = Assert.Single(PatchMatchChecker.FindUnmatched(patches, Rendered));
        Assert.Equal("bogus.yaml", unmatched.Path);
    }

    [Fact]
    public void A_patch_targeting_the_right_kind_but_wrong_name_is_flagged()
    {
        var patches = new[] { new PatchDeclaration("bogus.yaml", "Deployment", "worker") };

        Assert.Single(PatchMatchChecker.FindUnmatched(patches, Rendered));
    }

    [Fact]
    public void A_patch_with_no_declared_target_is_unverifiable_not_flagged()
    {
        var patches = new[] { new PatchDeclaration("labelselector.yaml", null, null) };

        Assert.Empty(PatchMatchChecker.FindUnmatched(patches, Rendered));
    }

    [Fact]
    public void A_kind_only_target_matches_any_name()
    {
        var patches = new[] { new PatchDeclaration("all-deployments.yaml", "Deployment", null) };

        Assert.Empty(PatchMatchChecker.FindUnmatched(patches, Rendered));
    }
}
