using Kontena.Plugins.ManifestStudio.Schemas;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Plugins.ManifestStudio.Tests.Schemas;

/// <summary>
/// A trimmed but shaped-like-the-real-thing OpenAPI v3 fragment: enough <c>$ref</c>s, a real
/// <c>x-kubernetes-group-version-kind</c>, and one deliberate cycle to exercise what
/// <see cref="OpenApiV3Document"/> actually has to survive against a real cluster.
/// </summary>
public sealed class OpenApiV3DocumentTests
{
    private const string Fixture = """
    {
      "components": {
        "schemas": {
          "io.k8s.api.apps.v1.Deployment": {
            "type": "object",
            "description": "Deployment enables declarative updates for Pods and ReplicaSets.",
            "required": ["spec"],
            "properties": {
              "apiVersion": { "type": "string" },
              "metadata": { "$ref": "#/components/schemas/io.k8s.apimachinery.pkg.apis.meta.v1.ObjectMeta" },
              "spec": { "$ref": "#/components/schemas/io.k8s.api.apps.v1.DeploymentSpec" }
            },
            "x-kubernetes-group-version-kind": [
              { "group": "apps", "version": "v1", "kind": "Deployment" }
            ]
          },
          "io.k8s.api.apps.v1.DeploymentSpec": {
            "type": "object",
            "required": ["selector", "template"],
            "properties": {
              "replicas": { "type": "integer" },
              "strategy": { "type": "string", "enum": ["Recreate", "RollingUpdate"] }
            }
          },
          "io.k8s.apimachinery.pkg.apis.meta.v1.ObjectMeta": {
            "type": "object",
            "properties": {
              "name": { "type": "string" },
              "namespace": { "type": "string" }
            }
          },
          "test.example.com.v1.Recursive": {
            "type": "object",
            "properties": {
              "children": {
                "type": "array",
                "items": { "$ref": "#/components/schemas/test.example.com.v1.Recursive" }
              }
            },
            "x-kubernetes-group-version-kind": [
              { "group": "test.example.com", "version": "v1", "kind": "Recursive" }
            ]
          }
        }
      }
    }
    """;

    private static readonly GroupVersionKind Deployment = new("apps", "v1", "Deployment");

    private OpenApiV3Document Document { get; } = OpenApiV3Document.Parse(Fixture);

    [Fact]
    public void A_kind_is_found_by_its_gvk_extension_not_the_schema_name()
    {
        var schema = Document.Resolve(Deployment);

        Assert.NotNull(schema);
        Assert.Equal("object", schema!.Type);
        Assert.Equal(
            "Deployment enables declarative updates for Pods and ReplicaSets.", schema.Description);
    }

    [Fact]
    public void An_unmodelled_kind_returns_null_not_an_exception()
    {
        Assert.Null(Document.Resolve(new GroupVersionKind("apps", "v1", "NoSuchKind")));
        Assert.Null(Document.Resolve(new GroupVersionKind("not.a.group", "v1", "Deployment")));
    }

    [Fact]
    public void Required_fields_are_captured()
    {
        var schema = Document.Resolve(Deployment)!;

        Assert.Contains("spec", schema.Required);
        Assert.DoesNotContain("apiVersion", schema.Required);
    }

    [Fact]
    public void A_ref_property_resolves_to_the_referenced_schemas_own_fields()
    {
        var schema = Document.Resolve(Deployment)!;

        var metadata = schema.Properties["metadata"];
        Assert.Equal("object", metadata.Type);
        Assert.Equal(["name", "namespace"], metadata.Properties.Keys.Order(StringComparer.Ordinal));

        var spec = schema.Properties["spec"];
        Assert.Contains("selector", spec.Required);
        Assert.Equal("integer", spec.Properties["replicas"].Type);
    }

    [Fact]
    public void Enum_values_ride_along_on_the_property_they_constrain()
    {
        var spec = Document.Resolve(Deployment)!.Properties["spec"];

        Assert.Equal(["Recreate", "RollingUpdate"], spec.Properties["strategy"].Enum);
    }

    [Fact]
    public void A_schema_that_refers_back_to_itself_resolves_without_recursing_forever()
    {
        var recursive = Document.Resolve(new GroupVersionKind("test.example.com", "v1", "Recursive"))!;

        var childItem = recursive.Properties["children"].Items;

        Assert.Same(recursive, childItem);
    }
}
