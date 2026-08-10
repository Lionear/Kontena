using Kontena.Sdk.Errors;
using Xunit;

namespace Kontena.Adapters.Docker.Tests;

/// <summary>
/// Which endpoint <c>DOCKER_HOST</c> resolves to (KON-359).
/// <para>
/// Pure, and deliberately not reading the real environment variable: these run in the same process as
/// every other test, so setting it for real would leak into whatever ran next. The resolver takes the
/// value as an argument for exactly that reason.
/// </para>
/// </summary>
public class DockerHostEndpointTests
{
    [Theory]
    [InlineData("unix:///Users/someone/.docker/run/docker.sock")]
    [InlineData("tcp://10.0.0.4:2375")]
    [InlineData("http://10.0.0.4:2375")]
    [InlineData("https://10.0.0.4:2376")]
    [InlineData("npipe://./pipe/docker_engine")]
    public void An_endpoint_the_client_can_speak_is_used_as_given(string host) =>
        Assert.Equal(new Uri(host), DockerEngine.ResolveEndpoint(host));

    [Fact]
    public void Surrounding_whitespace_does_not_make_it_unusable() =>
        Assert.Equal(new Uri("tcp://10.0.0.4:2375"), DockerEngine.ResolveEndpoint("  tcp://10.0.0.4:2375  "));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_set_falls_back_to_the_platform_socket(string? host)
    {
        var expected = OperatingSystem.IsWindows() ? "npipe://./pipe/docker_engine" : "unix:///var/run/docker.sock";

        Assert.Equal(new Uri(expected), DockerEngine.ResolveEndpoint(host));
    }

    /// <summary>
    /// ssh:// is a real Docker setup that Docker.DotNet cannot speak. It must say so, and point at the
    /// remote-engine route that does work — reporting it as an unreachable endpoint would send the user
    /// looking for a daemon that is running perfectly well.
    /// </summary>
    [Fact]
    public void An_ssh_endpoint_names_the_remote_route_instead()
    {
        var ex = Assert.Throws<EngineUnreachableException>(
            () => DockerEngine.ResolveEndpoint("ssh://docker@build-01"));

        Assert.Contains("ssh://docker@build-01", ex.Message, StringComparison.Ordinal);
        Assert.Contains("remote engine", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Never a silent fall back to the platform socket: that would answer from somewhere the user did
    /// not point at, which is the confusion this whole change exists to remove.
    /// </summary>
    [Theory]
    [InlineData("/var/run/docker.sock")]
    [InlineData("nonsense")]
    [InlineData("ftp://10.0.0.4")]
    public void An_endpoint_that_cannot_be_connected_to_is_refused_rather_than_replaced(string host)
    {
        var ex = Assert.Throws<EngineUnreachableException>(() => DockerEngine.ResolveEndpoint(host));

        Assert.Contains(host, ex.Message, StringComparison.Ordinal);
    }
}
