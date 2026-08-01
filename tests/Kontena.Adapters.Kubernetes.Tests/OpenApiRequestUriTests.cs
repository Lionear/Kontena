using Kontena.Adapters.Kubernetes;

namespace Kontena.Adapters.Kubernetes.Tests;

/// <summary>
/// The URI-building half of <see cref="KubernetesClusterEngine.GetOpenApiSchemaAsync"/> (KON-288) —
/// same core/named-group split and trailing-slash care as <c>ResourceTables.RequestUri</c>, checked
/// the same way: as a pure function, no server needed.
/// </summary>
public sealed class OpenApiRequestUriTests
{
    [Theory]
    [InlineData("", "v1", "https://10.0.0.2:6443/openapi/v3/api/v1")]
    [InlineData("apps", "v1", "https://10.0.0.2:6443/openapi/v3/apis/apps/v1")]
    [InlineData("cert-manager.io", "v1", "https://10.0.0.2:6443/openapi/v3/apis/cert-manager.io/v1")]
    public void The_core_group_and_named_groups_take_different_paths(string group, string version, string expected)
    {
        var uri = KubernetesClusterEngine.OpenApiRequestUri(new Uri("https://10.0.0.2:6443"), group, version);

        Assert.True(uri.IsAbsoluteUri);
        Assert.Equal(expected, uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://10.0.0.2:6443")]
    [InlineData("https://10.0.0.2:6443/")]
    public void A_trailing_slash_on_the_base_address_makes_no_difference(string baseUri)
    {
        var uri = KubernetesClusterEngine.OpenApiRequestUri(new Uri(baseUri), "apps", "v1");

        Assert.Equal("https://10.0.0.2:6443/openapi/v3/apis/apps/v1", uri.AbsoluteUri);
    }
}
