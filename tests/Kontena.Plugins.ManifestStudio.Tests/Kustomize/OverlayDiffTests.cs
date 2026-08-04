using Kontena.Plugins.ManifestStudio.Kustomize;

namespace Kontena.Plugins.ManifestStudio.Tests.Kustomize;

public sealed class OverlayDiffTests : IDisposable
{
    private readonly string _overlayDir = Directory.CreateTempSubdirectory("manifest-studio-overlay-diff-").FullName;

    public void Dispose() => Directory.Delete(_overlayDir, recursive: true);

    private static readonly KustomizationDirectives Empty =
        new(null, null, new Dictionary<string, string>(), new Dictionary<string, string>(), []);

    [Fact]
    public void A_name_change_matching_the_declared_prefix_is_attributed()
    {
        const string @base = "apiVersion: v1\nkind: Service\nmetadata:\n  name: web\n";
        const string overlay = "apiVersion: v1\nkind: Service\nmetadata:\n  name: prod-web\n";
        var directives = Empty with { NamePrefix = "prod-" };

        var diff = Assert.Single(OverlayDiff.Compare(@base, overlay, directives, _overlayDir));

        Assert.Equal(DiffCause.NamePrefix, diff.Cause);
        Assert.Equal("prod-", diff.Detail);
    }

    [Fact]
    public void A_name_change_matching_the_declared_suffix_is_attributed()
    {
        const string @base = "kind: Service\nmetadata:\n  name: web\n";
        const string overlay = "kind: Service\nmetadata:\n  name: web-v2\n";
        var directives = Empty with { NameSuffix = "-v2" };

        var diff = Assert.Single(OverlayDiff.Compare(@base, overlay, directives, _overlayDir));

        Assert.Equal(DiffCause.NameSuffix, diff.Cause);
        Assert.Equal("-v2", diff.Detail);
    }

    [Fact]
    public void A_name_change_that_does_not_match_any_declared_prefix_or_suffix_is_unexplained()
    {
        const string @base = "kind: Service\nmetadata:\n  name: web\n";
        const string overlay = "kind: Service\nmetadata:\n  name: something-else\n";
        var directives = Empty with { NamePrefix = "prod-" };

        var diff = Assert.Single(OverlayDiff.Compare(@base, overlay, directives, _overlayDir));

        Assert.Equal(DiffCause.Unexplained, diff.Cause);
    }

    [Fact]
    public void An_added_label_matching_commonLabels_is_attributed()
    {
        const string @base = "kind: Service\nmetadata:\n  name: web\n  labels:\n    app: web\n";
        const string overlay = "kind: Service\nmetadata:\n  name: web\n  labels:\n    app: web\n    env: prod\n";
        var directives = Empty with { CommonLabels = new Dictionary<string, string> { ["env"] = "prod" } };

        var diff = Assert.Single(OverlayDiff.Compare(@base, overlay, directives, _overlayDir));

        Assert.Equal(DiffCause.CommonLabel, diff.Cause);
        Assert.Equal("env", diff.Detail);
    }

    [Fact]
    public void An_added_annotation_matching_commonAnnotations_is_attributed()
    {
        const string @base = "kind: Service\nmetadata:\n  name: web\n";
        const string overlay = "kind: Service\nmetadata:\n  name: web\n  annotations:\n    owner: sre\n";
        var directives = Empty with { CommonAnnotations = new Dictionary<string, string> { ["owner"] = "sre" } };

        var diff = OverlayDiff.Compare(@base, overlay, directives, _overlayDir);

        Assert.Contains(diff, d => d.Cause == DiffCause.CommonAnnotation && d.Detail == "owner");
    }

    [Fact]
    public void A_change_matching_a_patch_targeting_this_resource_is_attributed_to_that_patch()
    {
        File.WriteAllText(Path.Combine(_overlayDir, "replicas.yaml"), "spec:\n  replicas: 5\n");
        const string @base = "kind: Deployment\nmetadata:\n  name: web\nspec:\n  replicas: 3\n";
        const string overlay = "kind: Deployment\nmetadata:\n  name: web\nspec:\n  replicas: 5\n";
        var directives = Empty with
        {
            Patches = [new PatchDeclaration("replicas.yaml", "Deployment", "web")],
        };

        var diff = Assert.Single(OverlayDiff.Compare(@base, overlay, directives, _overlayDir));

        Assert.Equal(DiffCause.Patch, diff.Cause);
        Assert.Equal("replicas.yaml", diff.Detail);
    }

    [Fact]
    public void A_matching_target_whose_patch_file_does_not_explain_the_line_stays_unexplained()
    {
        // The patch targets this Deployment, but its content has nothing to do with replicas — crediting
        // it anyway would be a guess, not a verified cause.
        File.WriteAllText(Path.Combine(_overlayDir, "other.yaml"), "metadata:\n  labels:\n    tier: web\n");
        const string @base = "kind: Deployment\nmetadata:\n  name: web\nspec:\n  replicas: 3\n";
        const string overlay = "kind: Deployment\nmetadata:\n  name: web\nspec:\n  replicas: 5\n";
        var directives = Empty with
        {
            Patches = [new PatchDeclaration("other.yaml", "Deployment", "web")],
        };

        var diff = Assert.Single(OverlayDiff.Compare(@base, overlay, directives, _overlayDir));

        Assert.Equal(DiffCause.Unexplained, diff.Cause);
    }

    [Fact]
    public void A_patch_targeting_a_different_resource_is_never_credited()
    {
        File.WriteAllText(Path.Combine(_overlayDir, "replicas.yaml"), "spec:\n  replicas: 5\n");
        const string @base = "kind: Deployment\nmetadata:\n  name: web\nspec:\n  replicas: 3\n";
        const string overlay = "kind: Deployment\nmetadata:\n  name: web\nspec:\n  replicas: 5\n";
        var directives = Empty with
        {
            // Targets a different Deployment ("worker") — should not explain "web"'s change.
            Patches = [new PatchDeclaration("replicas.yaml", "Deployment", "worker")],
        };

        var diff = Assert.Single(OverlayDiff.Compare(@base, overlay, directives, _overlayDir));

        Assert.Equal(DiffCause.Unexplained, diff.Cause);
    }

    [Fact]
    public void Unchanged_lines_never_appear_in_the_result()
    {
        const string @base = "kind: Service\nmetadata:\n  name: web\nspec:\n  type: ClusterIP\n";
        const string overlay = "kind: Service\nmetadata:\n  name: prod-web\nspec:\n  type: ClusterIP\n";
        var directives = Empty with { NamePrefix = "prod-" };

        var diff = OverlayDiff.Compare(@base, overlay, directives, _overlayDir);

        Assert.Single(diff);
        Assert.DoesNotContain(diff, d => d.Line.OverlayText == "spec:");
    }

    [Fact]
    public void Multiple_documents_in_a_bundle_are_paired_by_position()
    {
        const string @base = "kind: Deployment\nmetadata:\n  name: web\n---\nkind: Service\nmetadata:\n  name: web\n";
        const string overlay = "kind: Deployment\nmetadata:\n  name: prod-web\n---\nkind: Service\nmetadata:\n  name: prod-web\n";
        var directives = Empty with { NamePrefix = "prod-" };

        var diff = OverlayDiff.Compare(@base, overlay, directives, _overlayDir);

        Assert.Equal(2, diff.Count);
        Assert.All(diff, d => Assert.Equal(DiffCause.NamePrefix, d.Cause));
    }

    [Fact]
    public void An_extra_resource_the_overlay_adds_does_not_crash_the_comparison()
    {
        const string @base = "kind: Deployment\nmetadata:\n  name: web\n";
        const string overlay = "kind: Deployment\nmetadata:\n  name: web\n---\nkind: ConfigMap\nmetadata:\n  name: generated-abc123\n";

        var diff = OverlayDiff.Compare(@base, overlay, Empty, _overlayDir);

        Assert.Empty(diff); // the one shared document matches exactly; the extra one is out of scope.
    }
}
