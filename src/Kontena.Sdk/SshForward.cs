using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Kontena.Sdk;

/// <summary>
/// The local end of an SSH forward — where Kontena listens, and what a Docker client should be
/// pointed at (KON-258).
/// <para>
/// A unix socket where there is one, and a loopback TCP port on Windows. Not a style choice:
/// Win32-OpenSSH has no <c>streamlocal-forward-listen</c>, so the local half of <c>-L</c> cannot be a
/// socket file there at all — and a Windows path in that position is read as
/// <c>bind-address:port</c>, giving <c>Bad local forwarding specification 'C:\Users\…'</c> before any
/// of it is even attempted.
/// </para>
/// <para>
/// The <b>remote</b> half stays a unix socket on every platform. That side is served by the remote
/// host's own sshd, which supports it, and it is what <c>DOCKER_HOST=ssh://…</c> does too.
/// </para>
/// </summary>
/// <param name="Spec">What goes before the remote socket in <c>-L</c>.</param>
/// <param name="Endpoint">Where the Docker client connects.</param>
/// <param name="SocketPath">The socket file to clean up afterwards, or null when there is none.</param>
public sealed record SshForward(string Spec, Uri Endpoint, string? SocketPath)
{
    /// <summary>A unix socket, which is what every platform but Windows uses.</summary>
    public static SshForward OverSocket(string path) =>
        new(path, new Uri($"unix://{path}"), path);

    /// <summary>
    /// A loopback TCP port.
    /// <para>
    /// Bound to <c>127.0.0.1</c> explicitly, never left to ssh's default: <c>-L &lt;port&gt;:…</c>
    /// listens on the wildcard address under <c>GatewayPorts</c>, and a Docker socket reachable from
    /// the network is the whole machine handed to anyone who can reach the port.
    /// </para>
    /// </summary>
    public static SshForward OverLoopback(int port) =>
        new(
            $"127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}",
            new Uri($"tcp://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}"),
            SocketPath: null);

    /// <summary>Whether this forward listens on a socket file rather than a port.</summary>
    public bool IsSocket => SocketPath is not null;

    /// <summary>
    /// A port nothing is listening on, asked of the OS rather than guessed.
    /// <para>
    /// There is a gap between letting go of it here and ssh binding it, and nothing can close that
    /// gap — ssh takes a port number, not a socket. Losing the race means ssh fails to bind and says
    /// so, which the caller can retry; it is not a silent wrong answer.
    /// </para>
    /// </summary>
    public static int FreeLoopbackPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();

        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
