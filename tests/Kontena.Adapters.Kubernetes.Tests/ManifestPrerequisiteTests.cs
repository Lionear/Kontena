namespace Kontena.Adapters.Kubernetes.Tests;

/// <summary>
/// What a bundle has to create before the rest of itself can be validated (KON-380): the namespaces
/// it declares, and the kinds its CRDs define.
/// <para>
/// The reported case was <c>helm template prometheus/kube-prometheus-stack --include-crds</c>: ten
/// CRDs followed by fifty resources that use them. A dry-run persists nothing, so the API server has
/// never heard of <c>PrometheusRule</c> when it is asked to validate one — eighty red rows about a
/// manifest that is perfectly fine. These cover the reading that tells the two apart.
/// </para>
/// </summary>
public class ManifestPrerequisiteTests
{
    private const string Crd = """
        apiVersion: apiextensions.k8s.io/v1
        kind: CustomResourceDefinition
        metadata:
          name: prometheusrules.monitoring.coreos.com
        spec:
          group: monitoring.coreos.com
          names:
            kind: PrometheusRule
            plural: prometheusrules
        """;

    private const string Rule = """
        apiVersion: monitoring.coreos.com/v1
        kind: PrometheusRule
        metadata:
          name: node-rules
          namespace: monitoring
        """;

    [Fact]
    public void A_crd_registers_the_kind_it_defines()
    {
        var prerequisites = ManifestDocuments.PrerequisitesIn(ManifestDocuments.Split(Crd + "\n---\n" + Rule));

        Assert.Contains("monitoring.coreos.com/PrometheusRule", prerequisites.CustomKinds);
    }

    [Fact]
    public void A_namespace_the_bundle_creates_is_a_prerequisite_too()
    {
        var yaml = "apiVersion: v1\nkind: Namespace\nmetadata:\n  name: monitoring\n---\n" + Rule;

        var prerequisites = ManifestDocuments.PrerequisitesIn(ManifestDocuments.Split(yaml));

        Assert.Contains("monitoring", prerequisites.Namespaces);
    }

    [Fact]
    public void Only_namespaces_and_crds_go_first()
    {
        var documents = ManifestDocuments.Split(Crd + "\n---\n" + Rule).ToList();

        Assert.Collection(
            documents.Select(ManifestDocuments.IsPrerequisite),
            first => Assert.True(first),
            second => Assert.False(second));
    }

    /// <summary>
    /// The ordering itself: a CRD in the middle of a bundle still gets applied before the resources
    /// that need it, because the split is by kind rather than by position.
    /// </summary>
    [Fact]
    public void The_prerequisite_split_reorders_a_bundle_that_declares_the_crd_last()
    {
        var documents = ManifestDocuments.Split(Rule + "\n---\n" + Crd).ToList();

        var ordered = documents.Where(ManifestDocuments.IsPrerequisite)
            .Concat(documents.Where(d => !ManifestDocuments.IsPrerequisite(d)))
            .Select(d => ManifestDocuments.KindOf(d.Content!)!.Value.Kind)
            .ToList();

        Assert.Equal(["CustomResourceDefinition", "PrometheusRule"], ordered);
    }

    /// <summary>
    /// helm writes a <c># Source:</c> header per template, and a chart whose CRD files open with
    /// their own comments strands those headers between two <c>---</c> markers. Ten of them turned
    /// into ten "the document is empty" failures, which alone was enough to disable Apply.
    /// </summary>
    [Fact]
    public void A_chunk_of_nothing_but_comments_is_not_a_document()
    {
        var yaml = """
            # Source: kube-prometheus-stack/charts/crds/crds/crd-probes.yaml
            # https://raw.githubusercontent.com/prometheus-operator/...
            ---
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: dashboards
            """;

        var only = Assert.Single(ManifestDocuments.Split(yaml));

        Assert.Null(only.Error);
        Assert.Equal("ConfigMap", ManifestDocuments.KindOf(only.Content!)!.Value.Kind);
    }

    [Fact]
    public void A_comment_above_a_resource_still_leaves_it_a_document()
    {
        var only = Assert.Single(ManifestDocuments.Split("# a note\napiVersion: v1\nkind: ConfigMap\n"));

        Assert.Equal("ConfigMap", ManifestDocuments.KindOf(only.Content!)!.Value.Kind);
    }
}
