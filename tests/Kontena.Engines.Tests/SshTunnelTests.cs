using Kontena.Core.Models;
using Kontena.Engines;

namespace Kontena.Engines.Tests;

/// <summary>
/// The ssh command line. Asserted rather than read back, because every mistake in it is either a hang or
/// a silent fallback to the wrong thing — and none of it is visible from the app's side.
/// </summary>
public class SshTunnelTests
{
    private static RemoteEngine Remote(
        string host = "build-01", string? user = "rick", int? port = null, string? socket = null) =>
        new("r1", "Build server", RemoteEngineTransport.Ssh, host, port, user, socket);

    private static string Line(RemoteEngine remote) =>
        string.Join(' ', SshTunnel.Arguments(remote, "/run/user/1000/kontena-r1.sock"));

    [Fact]
    public void Forwards_the_local_socket_to_the_engine_socket() =>
        Assert.Contains("-L /run/user/1000/kontena-r1.sock:/var/run/docker.sock", Line(Remote()), StringComparison.Ordinal);

    [Fact]
    public void A_custom_remote_socket_is_used_as_given() =>
        // Rootless Podman lives somewhere else entirely, so this cannot be hardcoded.
        Assert.Contains(
            "/run/user/1000/podman/podman.sock",
            Line(Remote(socket: "/run/user/1000/podman/podman.sock")),
            StringComparison.Ordinal);

    [Fact]
    public void Asks_for_no_shell_and_no_terminal()
    {
        // The connection exists to carry a socket; a shell would keep it alive for the wrong reason and a
        // terminal would let ssh try to prompt.
        var line = Line(Remote());

        Assert.Contains("-N", line, StringComparison.Ordinal);
        Assert.Contains("-T", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Never_prompts() =>
        // With no terminal a password prompt is a hang rather than an error. BatchMode turns it into a
        // failure that says what is wrong.
        Assert.Contains("BatchMode=yes", Line(Remote()), StringComparison.Ordinal);

    [Fact]
    public void Gives_up_rather_than_hanging_on_a_dead_host() =>
        Assert.Contains("ConnectTimeout=10", Line(Remote()), StringComparison.Ordinal);

    [Fact]
    public void Notices_a_connection_that_has_gone_quiet()
    {
        // A tunnel that died silently looks exactly like an engine that stopped answering, and the
        // difference matters when telling the user what happened.
        var line = Line(Remote());

        Assert.Contains("ServerAliveInterval=15", line, StringComparison.Ordinal);
        Assert.Contains("ServerAliveCountMax=3", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Passes_user_and_host_the_way_ssh_expects() =>
        Assert.Equal("rick@build-01", SshTunnel.Arguments(Remote(), "/tmp/s.sock")[^1]);

    [Fact]
    public void Without_a_user_ssh_config_decides() =>
        // Dropping the user is what lets an ssh_config alias, and its User line, keep working.
        Assert.Equal("build-01", SshTunnel.Arguments(Remote(user: null), "/tmp/s.sock")[^1]);

    [Fact]
    public void A_port_is_passed_as_a_port_not_glued_to_the_host()
    {
        var arguments = SshTunnel.Arguments(Remote(port: 2222), "/tmp/s.sock");

        Assert.Contains("-p", arguments, StringComparer.Ordinal);
        Assert.Contains("2222", arguments, StringComparer.Ordinal);
        Assert.Equal("rick@build-01", arguments[^1]);
    }
}
