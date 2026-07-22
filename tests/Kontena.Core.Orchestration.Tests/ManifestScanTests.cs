using Kontena.Core.Orchestration.Rendering;
using Xunit;

namespace Kontena.Core.Orchestration.Tests;

/// <summary>
/// The shallow read that lets a render say something useful before the cluster is asked. It only
/// has to be right about identity and about the handful of mistakes a render can make.
/// </summary>
public class ManifestScanTests
{
    [Fact]
    public void Splits_a_bundle_and_reads_each_identity()
    {
        const string yaml = """
            apiVersion: v1
            kind: Namespace
            metadata:
              name: shop
            ---
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: web
              namespace: shop
              labels:
                name: not-the-name
            spec:
              replicas: 1
            """;

        var docs = ManifestScan.Split(yaml);

        Assert.Equal(2, docs.Count);
        Assert.Equal(("v1", "Namespace", "shop", ""), Tuple(docs[0]));
        Assert.Equal(("apps/v1", "Deployment", "web", "shop"), Tuple(docs[1]));
    }

    [Fact]
    public void Ignores_separators_that_belong_to_content()
    {
        // A '---' inside a block scalar is indented, so it is content, not a new document.
        const string yaml = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: notes
            data:
              readme: |
                ---
                a heading underline, not a document break
            """;

        var docs = ManifestScan.Split(yaml);

        Assert.Single(docs);
        Assert.Equal("notes", docs[0].Name);
    }

    [Fact]
    public void Drops_empty_and_comment_only_documents()
    {
        const string yaml = """
            # Source: chart/templates/nothing.yaml
            ---
            apiVersion: v1
            kind: Service
            metadata:
              name: web
            ---
            """;

        var docs = ManifestScan.Split(yaml);

        Assert.Single(docs);
        Assert.Equal("Service", docs[0].Kind);
    }

    [Fact]
    public void An_empty_render_is_an_error_not_an_empty_plan()
    {
        var found = ManifestScan.Check(ManifestScan.Split("   \n"));

        var error = Assert.Single(found, d => d.Severity == RenderSeverity.Error);
        Assert.Contains("no resources", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_documents_for_the_same_object_are_an_error()
    {
        const string yaml = """
            apiVersion: v1
            kind: Service
            metadata:
              name: web
              namespace: shop
            ---
            apiVersion: v1
            kind: Service
            metadata:
              name: web
              namespace: shop
            """;

        var found = ManifestScan.Check(ManifestScan.Split(yaml));

        var error = Assert.Single(found, d => d.Severity == RenderSeverity.Error);
        Assert.Contains("Service shop/web", error.Message, StringComparison.Ordinal);
        Assert.Contains("appears 2 times", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Same_name_in_a_different_namespace_is_not_a_clash()
    {
        const string yaml = """
            apiVersion: v1
            kind: Service
            metadata:
              name: web
              namespace: staging
            ---
            apiVersion: v1
            kind: Service
            metadata:
              name: web
              namespace: prod
            """;

        Assert.DoesNotContain(
            ManifestScan.Check(ManifestScan.Split(yaml)),
            d => d.Severity == RenderSeverity.Error);
    }

    [Fact]
    public void A_document_without_a_kind_cannot_be_applied()
    {
        const string yaml = """
            apiVersion: v1
            metadata:
              name: mystery
            """;

        var found = ManifestScan.Check(ManifestScan.Split(yaml));

        Assert.Contains(found, d => d.Severity == RenderSeverity.Error && d.Message.Contains("kind", StringComparison.Ordinal));
    }

    [Fact]
    public void A_generateName_counts_as_having_a_name()
    {
        const string yaml = """
            apiVersion: batch/v1
            kind: Job
            metadata:
              generateName: backfill-
            """;

        Assert.DoesNotContain(
            ManifestScan.Check(ManifestScan.Split(yaml)),
            d => d.Severity == RenderSeverity.Warning);
    }

    [Fact]
    public void The_summary_counts_what_was_rendered()
    {
        const string yaml = """
            apiVersion: v1
            kind: Service
            metadata:
              name: a
            ---
            apiVersion: v1
            kind: Service
            metadata:
              name: b
            ---
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: c
            """;

        var info = ManifestScan.Check(ManifestScan.Split(yaml)).Single(d => d.Severity == RenderSeverity.Info);

        Assert.Equal("3 resources · 2 Service, 1 Deployment", info.Message);
    }

    private static (string, string, string, string) Tuple(ScannedDoc doc) =>
        (doc.ApiVersion, doc.Kind, doc.Name, doc.Namespace);
}
