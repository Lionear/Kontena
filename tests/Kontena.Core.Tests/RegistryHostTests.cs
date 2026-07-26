using Kontena.Core.Models;
using Xunit;

namespace Kontena.Core.Tests;

/// <summary>
/// Matching a credential to an image is entirely down to this. Get it wrong in one direction and a
/// private pull goes out unauthenticated; wrong in the other and Kontena sends a registry login to
/// Docker Hub.
/// </summary>
public class RegistryHostTests
{
    [Theory]
    [InlineData("nginx", "docker.io")]
    [InlineData("nginx:1.27-alpine", "docker.io")]
    [InlineData("library/nginx", "docker.io")]
    // "lionear" is an account, not a host: no dot, no port, not localhost. Reading it as a registry is
    // the mistake that would make every Hub pull under an account look private.
    [InlineData("lionear/kontena:0.2.0", "docker.io")]
    [InlineData("ghcr.io/lionear/kontena", "ghcr.io")]
    [InlineData("GHCR.IO/Lionear/Kontena", "ghcr.io")]
    [InlineData("localhost:5000/app", "localhost:5000")]
    [InlineData("localhost/app", "localhost")]
    [InlineData("registry.local:5000/team/app:dev", "registry.local:5000")]
    [InlineData("quay.io/prometheus/node-exporter@sha256:abc", "quay.io")]
    [InlineData("", "docker.io")]
    public void Finds_the_registry_an_image_comes_from(string reference, string expected) =>
        Assert.Equal(expected, RegistryHost.For(reference));

    [Theory]
    [InlineData("docker.io")]
    [InlineData("index.docker.io")]
    [InlineData("registry-1.docker.io")]
    // What `docker login` actually writes into config.json for Hub — so a login inherited from there has
    // to be recognised as Hub, or it would never match a pull of "nginx".
    [InlineData("https://index.docker.io/v1/")]
    public void Every_spelling_of_docker_hub_is_docker_hub(string host) =>
        Assert.Equal("docker.io", RegistryHost.Canonical(host));

    [Fact]
    public void Hub_credentials_match_an_unqualified_image() =>
        Assert.True(RegistryHost.SameHost(
            RegistryHost.Canonical("https://index.docker.io/v1/"),
            RegistryHost.For("nginx:1.27")));

    [Fact]
    public void A_different_registry_is_not_a_match() =>
        // The consequence of getting this wrong is sending one registry's token to another.
        Assert.False(RegistryHost.SameHost("ghcr.io", "docker.io"));

    [Theory]
    [InlineData("ghcr.io", "GHCR.io")]
    [InlineData("registry.local:5000", "registry.local:5000/")]
    public void Spelling_differences_still_match(string a, string b) =>
        Assert.True(RegistryHost.SameHost(a, b));
}
