using System.Diagnostics;
using Kontena.Core.Models;

namespace Kontena.Engines;

/// <summary>
/// An SSH forward from a local unix socket to the engine's socket on a remote host (KON-46).
/// <para>
/// Docker's client protocol has no SSH transport — <c>DOCKER_HOST=ssh://…</c> works by tunnelling, and so
/// does this. The alternative would be reimplementing SSH in-process; using the system client instead
/// means the user's existing keys, agent, <c>ssh_config</c>, jump hosts and hardware tokens all keep
/// working, which is the whole reason SSH is the easy transport for them.
/// </para>
/// </summary>
public sealed class SshTunnel : IAsyncDisposable
{
    private readonly string _localSocket;
    private Process? _process;

    private SshTunnel(string localSocket) => _localSocket = localSocket;

    /// <summary>The local socket to point a Docker client at, once <see cref="OpenAsync"/> has returned.</summary>
    public Uri Endpoint => new($"unix://{_localSocket}");

    /// <summary>
    /// Opens the tunnel and waits for the local socket to exist. Throws when ssh exits first, carrying
    /// whatever ssh said — "Permission denied (publickey)" and "Host key verification failed" are the two
    /// most common failures, and both are far more useful than a timeout.
    /// </summary>
    public static async Task<SshTunnel> OpenAsync(
        RemoteEngine remote, TimeSpan timeout, CancellationToken ct = default)
    {
        // A socket per tunnel, under the runtime dir where sockets belong and are cleaned up on logout.
        var directory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")
            ?? Path.Combine(Path.GetTempPath(), "kontena");
        Directory.CreateDirectory(directory);

        var localSocket = Path.Combine(directory, $"kontena-{remote.Id}.sock");
        if (File.Exists(localSocket))
            File.Delete(localSocket);                        // a socket left by a previous run blocks bind

        var tunnel = new SshTunnel(localSocket);
        var start = new ProcessStartInfo("ssh")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        foreach (var argument in Arguments(remote, localSocket))
            start.ArgumentList.Add(argument);

        var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start ssh. Is an SSH client installed?");

        tunnel._process = process;

        try
        {
            await WaitForSocketAsync(localSocket, process, timeout, ct).ConfigureAwait(false);
            return tunnel;
        }
        catch (Exception)
        {
            await tunnel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// The ssh command line. Public for tests: this is the part that silently does the wrong thing, and
    /// it cannot be checked by reading the code back.
    /// </summary>
    public static IReadOnlyList<string> Arguments(RemoteEngine remote, string localSocket)
    {
        var arguments = new List<string>
        {
            // No shell, no terminal: this connection exists to carry a socket.
            "-N",
            "-T",

            // Fail rather than hang. Without this a dead host leaves the app waiting on a connect that
            // never times out, and the user has nothing to look at.
            "-o", "ConnectTimeout=10",

            // Keep it alive and notice when it is not: a tunnel that has silently died looks exactly like
            // an engine that has stopped answering, and the difference matters when reporting it.
            "-o", "ServerAliveInterval=15",
            "-o", "ServerAliveCountMax=3",

            // Never prompt. A password prompt would block on a process with no terminal, which is a hang
            // rather than an error; keys and agents are the supported path and saying so is clearer.
            "-o", "BatchMode=yes",

            "-L", $"{localSocket}:{remote.SocketPath ?? RemoteEngine.DefaultSocketPath}",
        };

        if (remote.Port is { } port)
        {
            arguments.Add("-p");
            arguments.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        arguments.Add(remote.User is { Length: > 0 } user ? $"{user}@{remote.Host}" : remote.Host);
        return arguments;
    }

    private static async Task WaitForSocketAsync(
        string socket, Process process, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (File.Exists(socket))
                return;

            if (process.HasExited)
            {
                // ssh's own message is the useful one — it names the reason.
                var error = (await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false)).Trim();
                throw new InvalidOperationException(
                    error.Length > 0 ? error : $"ssh exited with code {process.ExitCode}.");
            }

            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        throw new TimeoutException(
            "The SSH tunnel did not open in time. The host may be unreachable, or the remote socket path "
            + "may be wrong.");
    }

    public async ValueTask DisposeAsync()
    {
        var process = _process;
        _process = null;

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // Already gone, or not ours to kill any more.
            }
            finally
            {
                process.Dispose();
            }
        }

        try
        {
            // ssh does not always remove the socket it created, and a leftover blocks the next bind.
            if (File.Exists(_localSocket))
                File.Delete(_localSocket);
        }
        catch (Exception)
        {
            // Untidy, not harmful: the next open deletes it before binding.
        }
    }
}
