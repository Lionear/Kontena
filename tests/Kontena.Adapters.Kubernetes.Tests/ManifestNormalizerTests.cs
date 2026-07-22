using Kontena.Adapters.Kubernetes;

namespace Kontena.Adapters.Kubernetes.Tests;

/// <summary>
/// Normalization decides whether a diff is readable. Everything here is pure — no cluster.
/// </summary>
public class ManifestNormalizerTests
{
    private static Dictionary<string, object?> Doc(params (string Key, object? Value)[] entries) =>
        entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);

    [Fact]
    public void Server_managed_metadata_is_dropped()
    {
        // Left in, these change on every apply and bury the real difference.
        var yaml = ManifestNormalizer.ToComparableYaml(Doc(
            ("kind", "Deployment"),
            ("metadata", Doc(
                ("name", "web"),
                ("resourceVersion", "12345"),
                ("generation", 7L),
                ("uid", "abc-123"),
                ("creationTimestamp", "2026-01-01T00:00:00Z"),
                ("managedFields", new List<object?> { Doc(("manager", "kubectl")) })))));

        Assert.Contains("name: web", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("resourceVersion", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("generation", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("uid", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("creationTimestamp", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("managedFields", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_is_dropped_because_controllers_write_it_not_the_user()
    {
        var yaml = ManifestNormalizer.ToComparableYaml(Doc(
            ("kind", "Deployment"),
            ("spec", Doc(("replicas", 3L))),
            ("status", Doc(("readyReplicas", 3L)))));

        Assert.Contains("replicas: 3", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("readyReplicas", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Tooling_annotations_are_dropped_and_an_empty_block_disappears_with_them()
    {
        var yaml = ManifestNormalizer.ToComparableYaml(Doc(
            ("metadata", Doc(
                ("name", "web"),
                ("annotations", Doc(("kubectl.kubernetes.io/last-applied-configuration", "{...}")))))));

        Assert.DoesNotContain("last-applied-configuration", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("annotations", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void User_annotations_survive()
    {
        var yaml = ManifestNormalizer.ToComparableYaml(Doc(
            ("metadata", Doc(
                ("name", "web"),
                ("annotations", Doc(
                    ("team", "payments"),
                    ("kubectl.kubernetes.io/last-applied-configuration", "{...}")))))));

        Assert.Contains("team: payments", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("last-applied-configuration", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Keys_are_ordered_so_the_same_object_never_diffs_against_itself()
    {
        // The API server promises no key order; without sorting, two identical objects could differ.
        var one = ManifestNormalizer.ToComparableYaml(Doc(("b", 2L), ("a", 1L), ("c", 3L)));
        var other = ManifestNormalizer.ToComparableYaml(Doc(("c", 3L), ("b", 2L), ("a", 1L)));

        Assert.Equal(one, other);
        Assert.Equal("a: 1\nb: 2\nc: 3", one);
    }

    [Fact]
    public void Whole_numbers_render_as_integers()
    {
        // Boxed through the JSON/YAML round-trip these can come out as "2.0", which reads as a
        // change in a diff even when nothing changed.
        var yaml = ManifestNormalizer.ToComparableYaml(Doc(
            ("replicas", 2L), ("deadline", 600.0), ("ratio", 1.5)));

        Assert.Contains("replicas: 2", yaml, StringComparison.Ordinal);
        Assert.Contains("deadline: 600", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("600.0", yaml, StringComparison.Ordinal);
        Assert.Contains("ratio: 1.5", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void An_image_reference_is_not_quoted_but_an_ambiguous_scalar_is()
    {
        var yaml = ManifestNormalizer.ToComparableYaml(Doc(
            ("image", "nginx:1.27-alpine"),
            ("note", "key: value"),
            ("version", "1.5"),
            ("enabled", "true")));

        // A colon only makes YAML ambiguous when a space follows it.
        Assert.Contains("image: nginx:1.27-alpine", yaml, StringComparison.Ordinal);
        Assert.Contains("note: \"key: value\"", yaml, StringComparison.Ordinal);

        // Strings that would otherwise read back as a number or a bool keep their quotes.
        Assert.Contains("version: \"1.5\"", yaml, StringComparison.Ordinal);
        Assert.Contains("enabled: \"true\"", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Nested_lists_of_maps_render_readably()
    {
        var yaml = ManifestNormalizer.ToComparableYaml(Doc(
            ("containers", new List<object?>
            {
                Doc(("name", "web"), ("image", "nginx")),
                Doc(("name", "sidecar"), ("image", "envoy")),
            })));

        Assert.Equal(
            "containers:\n  - image: nginx\n    name: web\n  - image: envoy\n    name: sidecar",
            yaml);
    }

    [Fact]
    public void Empty_collections_stay_on_one_line()
    {
        var yaml = ManifestNormalizer.ToComparableYaml(Doc(
            ("resources", Doc()), ("args", new List<object?>())));

        Assert.Equal("args: []\nresources: {}", yaml);
    }

    [Fact]
    public void Anything_that_is_not_a_mapping_normalizes_to_nothing()
    {
        Assert.Empty(ManifestNormalizer.ToComparableYaml(null));
        Assert.Empty(ManifestNormalizer.ToComparableYaml("just a string"));
    }

    [Fact]
    public void A_secrets_values_never_reach_the_diff()
    {
        var yaml = ManifestNormalizer.ToComparableYaml(Doc(
            ("apiVersion", "v1"),
            ("kind", "Secret"),
            ("data", Doc(("password", "aHVudGVyMg=="))),
            ("stringData", Doc(("token", "s3cr3t")))));

        Assert.DoesNotContain("aHVudGVyMg==", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t", yaml, StringComparison.Ordinal);
        Assert.Contains("hidden (sha256:", yaml, StringComparison.Ordinal);

        // The keys stay: which secrets a bundle sets is not itself a secret.
        Assert.Contains("password:", yaml, StringComparison.Ordinal);
        Assert.Contains("token:", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void A_changed_secret_still_reads_as_changed()
    {
        var before = ManifestNormalizer.ToComparableYaml(Doc(
            ("kind", "Secret"), ("data", Doc(("password", "old")))));
        var after = ManifestNormalizer.ToComparableYaml(Doc(
            ("kind", "Secret"), ("data", Doc(("password", "new")))));

        // A digest, not a constant placeholder — otherwise a rotated secret would look unchanged.
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Only_secrets_are_masked()
    {
        var yaml = ManifestNormalizer.ToComparableYaml(Doc(
            ("kind", "ConfigMap"), ("data", Doc(("greeting", "hello")))));

        Assert.Contains("hello", yaml, StringComparison.Ordinal);
    }
}

public class ManifestDocumentsTests
{
    [Fact]
    public void Splits_a_bundle_on_document_markers()
    {
        var documents = ManifestDocuments.Split(
            "kind: A\napiVersion: v1\n---\nkind: B\napiVersion: v1\n").ToList();

        Assert.Equal(2, documents.Count);
        Assert.All(documents, d => Assert.Null(d.Error));
        Assert.Equal("A", documents[0].Content!["kind"]);
        Assert.Equal("B", documents[1].Content!["kind"]);
    }

    [Fact]
    public void A_leading_marker_and_blank_documents_are_ignored()
    {
        var documents = ManifestDocuments.Split("---\nkind: A\napiVersion: v1\n---\n\n---\n").ToList();

        var only = Assert.Single(documents);
        Assert.Equal("A", only.Content!["kind"]);
    }

    [Fact]
    public void Keys_are_strings_so_the_patch_body_can_be_serialized_as_json()
    {
        // The YAML reader hands back object-keyed maps, which JSON cannot express.
        var only = Assert.Single(ManifestDocuments.Split("metadata:\n  name: web\n  labels:\n    app: web\n"));

        var metadata = Assert.IsType<Dictionary<string, object?>>(only.Content!["metadata"]);
        Assert.Equal("web", metadata["name"]);
        Assert.IsType<Dictionary<string, object?>>(metadata["labels"]);
    }

    [Fact]
    public void A_broken_document_is_reported_rather_than_failing_the_bundle()
    {
        var documents = ManifestDocuments.Split("kind: A\napiVersion: v1\n---\n\tnot: [valid\n").ToList();

        Assert.Equal(2, documents.Count);
        Assert.Null(documents[0].Error);
        Assert.NotNull(documents[1].Error);
        Assert.Null(documents[1].Content);
    }
}
