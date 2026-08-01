using Kontena.Plugins.ManifestStudio.Schemas;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Plugins.ManifestStudio.Tests.Schemas;

/// <summary>
/// Against the real embedded resources, not fixtures — the whole point of KON-289 is that these are
/// genuine upstream Kubernetes documents, so a test that faked its own would prove nothing about
/// whether the real ones actually parse.
/// </summary>
public sealed class BundledSchemaSourceTests
{
    [Theory]
    [InlineData("1.36")]
    [InlineData("1.35")]
    [InlineData("1.34")]
    public async Task Every_bundled_minor_serves_a_resolvable_deployment_schema(string minor)
    {
        var source = new BundledSchemaSource(minor);

        var raw = await source.GetOpenApiSchemaAsync("apps", "v1");
        Assert.NotNull(raw);

        var schema = OpenApiV3Document.Parse(raw!).Resolve(new GroupVersionKind("apps", "v1", "Deployment"));
        Assert.NotNull(schema);
        Assert.Contains("spec", schema!.Properties.Keys);
    }

    [Fact]
    public async Task Every_bundled_group_resolves_at_least_one_kind()
    {
        // Each file is a real (trimmed) upstream document — spot-check one representative kind per
        // group so a future group addition that silently fails to bundle is caught here.
        var source = new BundledSchemaSource("1.36");
        var expectations = new (string Group, string Version, string Kind)[]
        {
            ("", "v1", "Pod"),
            ("apps", "v1", "Deployment"),
            ("batch", "v1", "Job"),
            ("networking.k8s.io", "v1", "Ingress"),
            ("rbac.authorization.k8s.io", "v1", "Role"),
            ("autoscaling", "v2", "HorizontalPodAutoscaler"),
            ("policy", "v1", "PodDisruptionBudget"),
            ("storage.k8s.io", "v1", "StorageClass"),
        };

        foreach (var (group, version, kind) in expectations)
        {
            var raw = await source.GetOpenApiSchemaAsync(group, version);
            Assert.True(raw is not null, $"{group}/{version} was not bundled.");

            var schema = OpenApiV3Document.Parse(raw!).Resolve(new GroupVersionKind(group, version, kind));
            Assert.True(schema is not null, $"{kind} not found in {group}/{version}.");
        }
    }

    [Fact]
    public async Task An_unbundled_group_is_unverifiable_not_an_error()
    {
        var source = new BundledSchemaSource("1.36");

        Assert.Null(await source.GetOpenApiSchemaAsync("cert-manager.io", "v1"));
    }

    [Fact]
    public async Task An_unbundled_minor_is_unverifiable_not_an_error()
    {
        var source = new BundledSchemaSource("1.10");

        Assert.Null(await source.GetOpenApiSchemaAsync("apps", "v1"));
    }

    [Fact]
    public async Task Server_version_identifies_itself_as_bundled()
    {
        var source = new BundledSchemaSource("1.35");

        Assert.Equal("bundled-1.35", await source.GetServerVersionAsync());
    }

    [Fact]
    public async Task With_no_minor_given_the_most_recent_bundled_one_is_used()
    {
        var source = new BundledSchemaSource();

        Assert.Equal("bundled-1.36", await source.GetServerVersionAsync());
        Assert.NotNull(await source.GetOpenApiSchemaAsync("apps", "v1"));
    }

    [Fact]
    public void The_banner_names_both_gaps_it_cannot_close()
    {
        Assert.Contains("custom resource", BundledSchemaSource.Banner, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("apiVersion", BundledSchemaSource.Banner, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolving_through_schemaindex_works_end_to_end_with_the_bundled_source()
    {
        var index = new SchemaIndex(new BundledSchemaSource("1.36"));

        var schema = await index.ResolveAsync(new GroupVersionKind("", "v1", "Pod"));

        Assert.NotNull(schema);
    }
}
