using Kontena.Plugins.ManifestStudio.Schemas;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Plugins.ManifestStudio.Tests.Schemas;

/// <summary>
/// The three authorities from Plan §5, each checked in isolation and then together in a bundle — same
/// real-pipeline-fixture approach as <see cref="CompletionEngineTests"/>.
/// </summary>
public sealed class ManifestDiagnosticsTests
{
    private const string Fixture = """
    {
      "components": {
        "schemas": {
          "test.Deployment": {
            "type": "object",
            "required": ["spec"],
            "properties": {
              "apiVersion": { "type": "string" },
              "kind": { "type": "string" },
              "metadata": { "$ref": "#/components/schemas/test.ObjectMeta" },
              "spec": { "$ref": "#/components/schemas/test.DeploymentSpec" }
            },
            "x-kubernetes-group-version-kind": [{ "group": "apps", "version": "v1", "kind": "Deployment" }]
          },
          "test.ObjectMeta": {
            "type": "object",
            "properties": { "name": { "type": "string" } }
          },
          "test.DeploymentSpec": {
            "type": "object",
            "required": ["selector", "template"],
            "properties": {
              "replicas": { "type": "integer" },
              "selector": { "type": "object" },
              "template": { "$ref": "#/components/schemas/test.PodTemplateSpec" }
            }
          },
          "test.PodTemplateSpec": {
            "type": "object",
            "properties": { "spec": { "$ref": "#/components/schemas/test.PodSpec" } }
          },
          "test.PodSpec": {
            "type": "object",
            "properties": {
              "containers": { "type": "array", "items": { "$ref": "#/components/schemas/test.Container" } }
            }
          },
          "test.Container": {
            "type": "object",
            "required": ["name", "image"],
            "properties": {
              "name": { "type": "string" },
              "image": { "type": "string" },
              "envFrom": { "type": "array", "items": { "$ref": "#/components/schemas/test.EnvFromSource" } }
            }
          },
          "test.EnvFromSource": {
            "type": "object",
            "properties": {
              "configMapRef": { "$ref": "#/components/schemas/test.LocalObjectReference" }
            }
          },
          "test.LocalObjectReference": {
            "type": "object",
            "properties": { "name": { "type": "string" } }
          }
        }
      }
    }
    """;

    private static readonly GroupVersionKind DeploymentGvk = new("apps", "v1", "Deployment");
    private static readonly JsonSchemaNode DeploymentSchema =
        OpenApiV3Document.Parse(Fixture).Resolve(DeploymentGvk)!;

    private static readonly IReadOnlyDictionary<GroupVersionKind, JsonSchemaNode?> Schemas =
        new Dictionary<GroupVersionKind, JsonSchemaNode?> { [DeploymentGvk] = DeploymentSchema };

    private static IReadOnlyList<Diagnostic> Validate(
        string bundle, IReadOnlyList<ApiResource>? discovered = null) =>
        ManifestDiagnostics.Validate(bundle, Schemas, discovered);

    // ── Schema authority ─────────────────────────────────────────────────────

    [Fact]
    public void An_unknown_field_is_a_schema_error_on_its_own_line()
    {
        var diagnostics = Validate("""
            apiVersion: apps/v1
            kind: Deployment
            spec:
              selector: {}
              template: {}
              bogus: yes
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticAuthority.Schema, diagnostic.Authority);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(5, diagnostic.Line);
        Assert.Contains("bogus", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_required_field_is_reported_on_the_parent_that_lacks_it()
    {
        var diagnostics = Validate("""
            apiVersion: apps/v1
            kind: Deployment
            spec:
              selector: {}
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("template", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(2, diagnostic.Line); // the "spec:" line itself — the object that is short a field.
    }

    [Fact]
    public void A_missing_top_level_required_field_is_reported_at_the_top_of_the_document()
    {
        var diagnostics = Validate("""
            apiVersion: apps/v1
            kind: Deployment
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("spec", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(0, diagnostic.Line);
    }

    [Fact]
    public void A_non_numeric_value_for_an_integer_field_is_a_type_error()
    {
        var diagnostics = Validate("""
            apiVersion: apps/v1
            kind: Deployment
            spec:
              replicas: many
              selector: {}
              template: {}
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("integer", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(3, diagnostic.Line);
    }

    [Fact]
    public void A_required_field_missing_inside_a_sequence_item_is_reported_on_that_item()
    {
        var diagnostics = Validate("""
            apiVersion: apps/v1
            kind: Deployment
            spec:
              selector: {}
              template:
                spec:
                  containers:
                  - image: nginx
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("name", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(7, diagnostic.Line); // the "- image: nginx" line — the item that is short a name.
    }

    [Fact]
    public void A_document_whose_kind_has_no_resolved_schema_is_unverifiable_not_flagged()
    {
        // No entry in Schemas for cert-manager.io/v1 Certificate — silence, not a crash.
        Assert.Empty(Validate("""
            apiVersion: cert-manager.io/v1
            kind: Certificate
            spec:
              anythingAtAll: true
            """));
    }

    // ── Cluster discovery authority ──────────────────────────────────────────

    private static readonly ApiResource DeploymentV1Beta1 = new()
    {
        Kind = new GroupVersionKind("apps", "v1beta1", "Deployment"), Plural = "deployments",
    };

    [Fact]
    public void A_deprecated_apiVersion_the_cluster_moved_away_from_is_a_warning()
    {
        var diagnostics = Validate("""
            apiVersion: apps/v1beta9
            kind: Deployment
            spec:
              selector: {}
              template: {}
            """, discovered: [DeploymentV1Beta1]);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticAuthority.ClusterDiscovery, diagnostic.Authority);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("v1beta1", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_kind_the_cluster_never_served_at_all_stays_unverifiable()
    {
        Assert.Empty(Validate("""
            apiVersion: cert-manager.io/v1
            kind: Certificate
            spec: {}
            """, discovered: [DeploymentV1Beta1]));
    }

    [Fact]
    public void No_discovery_data_at_all_means_no_cluster_discovery_diagnostics()
    {
        Assert.Empty(Validate("""
            apiVersion: apps/v1beta9
            kind: Deployment
            spec:
              selector: {}
              template: {}
            """, discovered: null));
    }

    // ── Cross-document authority ─────────────────────────────────────────────

    [Fact]
    public void A_configmap_reference_with_no_matching_document_is_a_hint_never_an_error()
    {
        var diagnostics = Validate("""
            apiVersion: apps/v1
            kind: Deployment
            spec:
              selector: {}
              template:
                spec:
                  containers:
                  - name: app
                    image: nginx
                    envFrom:
                    - configMapRef:
                        name: missing-config
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticAuthority.CrossDocument, diagnostic.Authority);
        Assert.Equal(DiagnosticSeverity.Hint, diagnostic.Severity);
        Assert.Contains("missing-config", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_configmap_reference_matched_elsewhere_in_the_bundle_is_not_flagged()
    {
        var diagnostics = Validate("""
            apiVersion: apps/v1
            kind: Deployment
            spec:
              selector: {}
              template:
                spec:
                  containers:
                  - name: app
                    image: nginx
                    envFrom:
                    - configMapRef:
                        name: app-config
            ---
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: app-config
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Line_numbers_in_the_second_document_of_a_bundle_are_absolute_not_relative()
    {
        var diagnostics = Validate("""
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: app-config
            ---
            apiVersion: apps/v1
            kind: Deployment
            spec:
              selector: {}
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("template", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(7, diagnostic.Line); // "spec:" is line 7 of the whole bundle, not line 2 of its own document.
    }
}
