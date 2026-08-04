using Kontena.Plugins.ManifestStudio.Schemas;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Plugins.ManifestStudio.Tests.Schemas;

/// <summary>
/// Textual cases against a real (trimmed) Deployment schema, resolved through the actual
/// <see cref="OpenApiV3Document"/> pipeline rather than hand-built <see cref="JsonSchemaNode"/>
/// trees — the same fixture style KON-150's diagnosis tests use, and no reason to trust a completion
/// engine against a schema shape that parsing itself was never proven to produce.
/// </summary>
public sealed class CompletionEngineTests
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
            "properties": { "name": { "type": "string" }, "namespace": { "type": "string" } }
          },
          "test.DeploymentSpec": {
            "type": "object",
            "required": ["selector", "template"],
            "properties": {
              "replicas": { "type": "integer" },
              "selector": { "type": "object" },
              "strategy": { "type": "string", "enum": ["Recreate", "RollingUpdate"] },
              "template": { "$ref": "#/components/schemas/test.PodTemplateSpec" }
            }
          },
          "test.PodTemplateSpec": {
            "type": "object",
            "properties": { "spec": { "$ref": "#/components/schemas/test.PodSpec" } }
          },
          "test.PodSpec": {
            "type": "object",
            "required": ["containers"],
            "properties": {
              "containers": { "type": "array", "items": { "$ref": "#/components/schemas/test.Container" } },
              "restartPolicy": { "type": "string" }
            }
          },
          "test.Container": {
            "type": "object",
            "required": ["name", "image"],
            "properties": {
              "name": { "type": "string" },
              "image": { "type": "string" },
              "ports": { "type": "array", "items": { "type": "object" } }
            }
          }
        }
      }
    }
    """;

    private static readonly JsonSchemaNode Deployment =
        OpenApiV3Document.Parse(Fixture).Resolve(new GroupVersionKind("apps", "v1", "Deployment"))!;

    private static IReadOnlyList<string> SuggestAt(string document, string caret)
    {
        var position = document.IndexOf(caret, StringComparison.Ordinal);
        Assert.True(position >= 0, $"Caret marker '{caret}' not found in the fixture document.");

        return [.. CompletionEngine.Suggest(document, position, Deployment).Select(s => s.Name)];
    }

    [Fact]
    public void No_schema_means_no_suggestions_not_an_exception() =>
        Assert.Empty(CompletionEngine.Suggest("anything", 0, schema: null));

    [Fact]
    public void An_empty_document_offers_the_roots_own_fields_required_first()
    {
        var names = SuggestAt("§", "§");

        Assert.Equal(["spec", "apiVersion", "kind", "metadata"], names);
    }

    [Fact]
    public void A_partial_top_level_key_filters_by_prefix_case_insensitively()
    {
        Assert.Equal(["apiVersion"], SuggestAt("API§", "§"));
    }

    [Fact]
    public void Nested_object_completion_walks_into_spec()
    {
        var names = SuggestAt("""
            apiVersion: apps/v1
            spec:
              §
            """, "§");

        Assert.Equal(["selector", "template", "replicas", "strategy"], names);
    }

    [Fact]
    public void An_unmodelled_key_path_is_unverifiable_not_a_crash()
    {
        Assert.Empty(SuggestAt("""
            notARealKey:
              §
            """, "§"));
    }

    [Fact]
    public void Enum_values_complete_a_value_in_progress_filtered_by_prefix()
    {
        var names = SuggestAt("""
            spec:
              strategy: Roll§
            """, "§");

        Assert.Equal(["RollingUpdate"], names);
    }

    [Fact]
    public void An_indented_sequence_item_resolves_through_two_levels_of_array_and_object()
    {
        // Broken on purpose: "ima" is an unfinished word, and there is no closing structure below the
        // cursor at all — path resolution above the cursor must not care.
        var names = SuggestAt("""
            spec:
              template:
                spec:
                  containers:
                    - name: app
                      ima§
            """, "§");

        Assert.Equal(["image"], names);
    }

    [Fact]
    public void A_flush_style_sequence_sitting_at_its_keys_own_indent_still_resolves()
    {
        // YAML allows "- " to sit flush with the key that owns it, no extra indent — a second, equally
        // valid style from the indented-items case above.
        var names = SuggestAt("""
            spec:
              template:
                spec:
                  containers:
                  - name: app
                    §
            """, "§");

        Assert.Equal(["image", "name", "ports"], names);
    }

    [Fact]
    public void A_second_sequence_item_does_not_stack_on_top_of_the_first()
    {
        var names = SuggestAt("""
            spec:
              template:
                spec:
                  containers:
                    - name: app
                      image: nginx
                    - §
            """, "§");

        Assert.Equal(["image", "name", "ports"], names);
    }

    [Fact]
    public void A_new_top_level_key_closes_the_whole_sequence_it_follows()
    {
        var names = SuggestAt("""
            spec:
              template:
                spec:
                  containers:
                  - name: app
                    image: nginx
            §
            """, "§");

        // "spec" comes back too — no sibling-exclusion (see the class doc) — but the point of this
        // test is that it comes back as a *root*-level suggestion: the whole nested chain closed.
        Assert.Equal(["spec", "apiVersion", "kind", "metadata"], names);
    }
}
