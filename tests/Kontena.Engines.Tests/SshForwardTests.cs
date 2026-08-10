using System.Net;
using System.Net.Sockets;
using Kontena.Sdk;
using Kontena.Sdk.Models;

namespace Kontena.Engines.Tests;

/// <summary>
/// The local end of the forward (KON-258).
/// <para>
/// Windows cannot listen on a unix socket — Win32-OpenSSH has no streamlocal-forward-listen — and a
/// Windows path in that position is not merely unsupported but misread: ssh splits <c>-L</c> on
/// colons, takes <c>C</c> for a bind address, and refuses with
/// <c>Bad local forwarding specification</c>. So the local half becomes a loopback port there, while
/// the remote half stays a unix socket everywhere.
/// </para>
/// </summary>
public class SshForwardTests
{
    private static RemoteEngine Remote() =>
        new("r1", "Build server", RemoteEngineTransport.Ssh, "build-01", 2222, "deploy");

    [Fact]
    public void A_socket_forward_is_pointed_at_with_a_unix_url()
    {
        var forward = SshForward.OverSocket("/run/user/1000/kontena-r1.sock");

        Assert.Equal("/run/user/1000/kontena-r1.sock", forward.Spec);
        Assert.Equal("unix", forward.Endpoint.Scheme, StringComparer.Ordinal);
        Assert.True(forward.IsSocket);
    }

    [Fact]
    public void A_port_forward_is_pointed_at_with_a_tcp_url()
    {
        var forward = SshForward.OverLoopback(54321);

        Assert.Equal("tcp", forward.Endpoint.Scheme, StringComparer.Ordinal);
        Assert.Equal(54321, forward.Endpoint.Port);
        Assert.False(forward.IsSocket);
    }

    [Fact]
    public void A_port_forward_binds_the_loopback_address_and_says_so()
    {
        // Without the address, "-L 54321:…" listens on the wildcard under GatewayPorts — and a Docker
        // socket reachable from the network is that host handed to anyone who can reach the port.
        Assert.Equal("127.0.0.1:54321", SshForward.OverLoopback(54321).Spec, StringComparer.Ordinal);
    }

    [Fact]
    public void A_port_forward_leaves_nothing_to_clean_up()
    {
        // The socket path is what Dispose deletes. A port has no file, and deleting one implied by a
        // number is how you delete something else.
        Assert.Null(SshForward.OverLoopback(54321).SocketPath);
    }

    [Fact]
    public void The_free_port_is_actually_free()
    {
        var port = SshForward.FreeLoopbackPort();

        // Asked of the OS rather than guessed, and released again before ssh is told about it — so it
        // has to be bindable right now.
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        listener.Stop();

        Assert.InRange(port, 1, 65535);
    }

    [Fact]
    public void The_remote_half_stays_a_unix_socket_whichever_end_is_local()
    {
        // That side is served by the remote host's sshd, which supports it — and it is what
        // DOCKER_HOST=ssh://… forwards to as well.
        var overPort = string.Join(' ', SshTunnel.Arguments(Remote(), "127.0.0.1:54321"));
        var overSocket = string.Join(' ', SshTunnel.Arguments(Remote(), "/tmp/s.sock"));

        Assert.Contains("-L 127.0.0.1:54321:/var/run/docker.sock", overPort, StringComparison.Ordinal);
        Assert.Contains("-L /tmp/s.sock:/var/run/docker.sock", overSocket, StringComparison.Ordinal);
    }

    [Fact]
    public void This_machine_gets_the_forward_its_ssh_can_bind()
    {
        // The assertion that actually catches the bug, and it only means something on Windows — where
        // it runs: ci.yml puts the suite on windows-latest and macos-latest as well. Note that it does
        // so for pull requests to main, so on a develop branch this half is asserted but not exercised.
        var forward = SshTunnel.LocalEnd(Remote());

        if (OperatingSystem.IsWindows())
        {
            // The failure this replaces: a path like C:\Users\…\kontena-<id>.sock, of which ssh reads
            // "C" as a bind address and the rest as nonsense.
            Assert.False(forward.IsSocket);
            Assert.DoesNotContain(":\\", forward.Spec, StringComparison.Ordinal);
            Assert.StartsWith("127.0.0.1:", forward.Spec, StringComparison.Ordinal);
        }
        else
        {
            Assert.True(forward.IsSocket);
        }
    }

    [Fact]
    public void Without_a_runtime_dir_the_socket_goes_somewhere_nobody_else_could_have_made()
    {
        // Windows has neither unix sockets here nor file modes to ask about.
        if (OperatingSystem.IsWindows())
            return;

        var directory = SshTunnel.ResolveSocketDirectory(_ => null);

        try
        {
            // The bug this replaces: a fixed /tmp/kontena, created with whatever the umask says, in a
            // directory every user on the machine can write to — so another user can own it first, and
            // what goes in it is the Docker API of a remote host.
            Assert.NotEqual(Path.Combine(Path.GetTempPath(), "kontena"), directory);
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(directory));
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void A_runtime_dir_is_used_as_it_is()
    {
        // It is the user's own, already owner-only, and swept at logout — there is nothing to improve
        // on, and making a directory inside it would only leave one behind.
        Assert.Equal("/run/user/1000", SshTunnel.ResolveSocketDirectory(_ => "/run/user/1000"));
    }
}
