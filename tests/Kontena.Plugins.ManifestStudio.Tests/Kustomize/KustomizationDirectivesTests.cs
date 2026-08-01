using Kontena.Plugins.ManifestStudio.Kustomize;

namespace Kontena.Plugins.ManifestStudio.Tests.Kustomize;

public sealed class KustomizationDirectivesTests
{
    [Fact]
    public void Absent_directives_are_all_empty_or_null()
    {
        var directives = KustomizationFile.ParseDirectives("resources:\n  - ../base\n");

        Assert.Null(directives.NamePrefix);
        Assert.Null(directives.NameSuffix);
        Assert.Empty(directives.CommonLabels);
        Assert.Empty(directives.CommonAnnotations);
        Assert.Empty(directives.Patches);
    }

    [Fact]
    public void NamePrefix_and_nameSuffix_are_read()
    {
        var directives = KustomizationFile.ParseDirectives("namePrefix: prod-\nnameSuffix: -v2\n");

        Assert.Equal("prod-", directives.NamePrefix);
        Assert.Equal("-v2", directives.NameSuffix);
    }

    [Fact]
    public void CommonLabels_and_commonAnnotations_are_read_as_maps()
    {
        var directives = KustomizationFile.ParseDirectives("""
            commonLabels:
              env: prod
              team: platform
            commonAnnotations:
              owner: sre
            """);

        Assert.Equal("prod", directives.CommonLabels["env"]);
        Assert.Equal("platform", directives.CommonLabels["team"]);
        Assert.Equal("sre", directives.CommonAnnotations["owner"]);
    }

    [Fact]
    public void Patches_are_still_read_alongside_the_other_directives()
    {
        var directives = KustomizationFile.ParseDirectives("""
            patches:
              - path: replicas.yaml
                target:
                  kind: Deployment
                  name: web
            """);

        var patch = Assert.Single(directives.Patches);
        Assert.Equal("Deployment", patch.TargetKind);
    }
}
