using Kontena.Core.Models;
using Xunit;

namespace Kontena.Core.Tests;

/// <summary>
/// What a remote connection means before anything is attempted. The security rule here is the important
/// one: an unauthenticated Docker socket on a network port hands control of that host to anyone who can
/// reach it, so it cannot be reachable by accident.
/// </summary>
public class RemoteEngineTests
{
    private static RemoteEngine Ssh(string host = "build-01", string? user = "deploy", int? port = null) =>
        new("r1", "Build server", RemoteEngineTransport.Ssh, host, port, user);

    private static RemoteEngine Tcp(
        string host = "build-01", int? port = null, string? certs = null, bool allowInsecure = false) =>
        new("r2", "Build server", RemoteEngineTransport.Tcp, host, port,
            CertificateDirectory: certs, AllowInsecureTcp: allowInsecure);

    [Fact]
    public void A_tcp_engine_without_certificates_is_refused() =>
        // Not a warning, not a default-on: it does not connect until someone says they mean it.
        Assert.NotNull(Tcp().Problem);

    [Fact]
    public void Certificates_make_a_tcp_engine_usable() =>
        Assert.Null(Tcp(certs: "/srv/docker/certs").Problem);

    [Fact]
    public void Insecure_tcp_is_possible_but_only_when_stated_outright() =>
        Assert.Null(Tcp(allowInsecure: true).Problem);

    [Fact]
    public void Ssh_needs_nothing_beyond_a_host() =>
        // The transport authenticates itself: existing keys, existing agent.
        Assert.Null(Ssh().Problem);

    [Fact]
    public void A_connection_without_a_host_is_refused() =>
        Assert.NotNull(Ssh(host: "  ").Problem);

    [Theory]
    [InlineData("deploy", null, "ssh://deploy@build-01")]
    [InlineData(null, null, "ssh://build-01")]
    [InlineData("deploy", 2222, "ssh://deploy@build-01:2222")]
    public void Describes_an_ssh_endpoint_the_way_docker_would(string? user, int? port, string expected) =>
        Assert.Equal(expected, Ssh(user: user, port: port).Endpoint);

    [Fact]
    public void A_tcp_endpoint_defaults_to_the_tls_port() =>
        // 2376 is the TLS port. 2375 is the unencrypted one and is deliberately not a default anywhere.
        Assert.Equal("tcp://build-01:2376", Tcp(certs: "/certs").Endpoint);

    [Fact]
    public void Each_remote_is_its_own_backend() =>
        Assert.NotEqual(Ssh().Backend, Tcp().Backend);

    [Fact]
    public void The_backend_id_survives_a_rename()
    {
        // Keyed by id, not name: the keychain entry and the remembered "open this on launch" choice must
        // not break because someone tidied up a label.
        var before = Ssh();
        var after = before with { Name = "Something else" };

        Assert.Equal(before.Backend, after.Backend);
    }
}
