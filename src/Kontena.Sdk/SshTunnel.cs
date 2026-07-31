using System.Diagnostics;
using Kontena.Sdk.Errors;
using Kontena.Sdk.Models;

namespace Kontena.Sdk;

/// <summary>
/// How ssh gets a password without a terminal to type it into (KON-259).
/// <para>
/// ssh runs <c>SSH_ASKPASS</c> and reads one line from its stdout. Kontena points that at
/// <b>itself</b> — the same executable, in a mode that answers and exits — rather than shipping a
/// second binary, which would be another artefact to sign and notarise (KON-53) for the sake of
/// twenty lines.
/// </para>
/// <para>
/// The name of a keychain entry travels in the environment; the password never does. It is not in
/// argv either, so it is not in <c>ps</c> — which is the whole reason <c>sshpass -p</c> is not the
/// answer here.
/// </para>
/// </summary>
/// <param name="Executable">Kontena's own path, as ssh should invoke it.</param>
/// <param name="SecretKey">Which keychain entry the helper should read.</param>
public sealed record SshAskpass(string Executable, string SecretKey)
{
    /// <summary>The variable that puts Kontena into askpass mode, and names the entry to read.</summary>
    public const string SecretVariable = "KONTENA_ASKPASS_SECRET";
}

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
    private readonly SshForward _forward;
    private Process? _process;

    private SshTunnel(SshForward forward) => _forward = forward;

    /// <summary>The address to point a Docker client at, once <see cref="OpenAsync"/> has returned.</summary>
    public Uri Endpoint => _forward.Endpoint;

    /// <summary>
    /// Opens the tunnel and waits for the local socket to exist. Throws when ssh exits first, carrying
    /// whatever ssh said — "Permission denied (publickey)" and "Host key verification failed" are the two
    /// most common failures, and both are far more useful than a timeout.
    /// </summary>
    /// <param name="askpass">
    /// How to answer a password prompt, for an engine configured to use one. Null everywhere else, and
    /// null is what keeps <c>BatchMode=yes</c> meaningful: no helper, no prompt, no hang.
    /// </param>
    public static async Task<SshTunnel> OpenAsync(
        RemoteEngine remote, TimeSpan timeout, SshAskpass? askpass = null, CancellationToken ct = default)
    {
        var forward = LocalEnd(remote);
        var tunnel = new SshTunnel(forward);
        var start = new ProcessStartInfo("ssh")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        foreach (var argument in Arguments(remote, forward.Spec))
            start.ArgumentList.Add(argument);

        if (remote.UsePassword && askpass is { } helper)
        {
            start.Environment["SSH_ASKPASS"] = helper.Executable;
            start.Environment[SshAskpass.SecretVariable] = helper.SecretKey;

            // Without this ssh only consults SSH_ASKPASS when it has no terminal *and* DISPLAY is set,
            // which is a rule about X11 that has nothing to do with whether this process can type.
            // Requires OpenSSH 8.4 or newer; on older clients the prompt goes nowhere and the attempt
            // fails, which is the behaviour we already had.
            start.Environment["SSH_ASKPASS_REQUIRE"] = "force";
        }

        var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start ssh. Is an SSH client installed?");

        tunnel._process = process;

        try
        {
            await WaitForReadyAsync(forward, process, remote, timeout, ct).ConfigureAwait(false);
            return tunnel;
        }
        catch (Exception)
        {
            await tunnel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Where this machine will listen — a unix socket, or a loopback port on Windows (KON-258).
    /// Public for tests: which of the two is chosen cannot be seen from the outside, and choosing
    /// wrong is a connection that never opens.
    /// </summary>
    public static SshForward LocalEnd(RemoteEngine remote)
    {
        ArgumentNullException.ThrowIfNull(remote);

        if (OperatingSystem.IsWindows())
            return SshForward.OverLoopback(SshForward.FreeLoopbackPort());

        // A socket per tunnel, under the runtime dir where sockets belong and are cleaned up on logout.
        var directory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")
            ?? Path.Combine(Path.GetTempPath(), "kontena");
        Directory.CreateDirectory(directory);

        var socket = Path.Combine(directory, $"kontena-{remote.Id}.sock");
        if (File.Exists(socket))
            File.Delete(socket);                             // a socket left by a previous run blocks bind

        return SshForward.OverSocket(socket);
    }

    /// <summary>
    /// The ssh command line. Public for tests: this is the part that silently does the wrong thing, and
    /// it cannot be checked by reading the code back.
    /// </summary>
    /// <param name="localEnd">
    /// The local half of <c>-L</c>: a socket path, or <c>127.0.0.1:port</c>. See <see cref="SshForward"/>.
    /// </param>
    public static IReadOnlyList<string> Arguments(RemoteEngine remote, string localEnd)
    {
        ArgumentNullException.ThrowIfNull(remote);

        // The last gate before a process starts (KON-181). The callers that reach a remote engine check
        // Problem first, so this should never fire — which is exactly why it is here: a future caller
        // that forgets should fail loudly rather than hand ssh an argument it reads as an option.
        if (RemoteEngine.ArgumentProblem(remote.Host, remote.User, remote.SocketPath, remote.KeyFile)
            is { } problem)
        {
            throw new ArgumentException(problem, nameof(remote));
        }

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
            //
            // Except where the user chose a password for this engine (KON-259). Then the prompt is
            // answered without a terminal, by SSH_ASKPASS — set on the process, not here — and leaving
            // BatchMode on would refuse the only method that was chosen.
            "-o", remote.UsePassword ? "BatchMode=no" : "BatchMode=yes",

            // The remote half is a unix socket on every platform: that end is served by the remote
            // host's sshd, which supports it. Only the local half varies (KON-258).
            "-L", $"{localEnd}:{remote.SocketPath ?? RemoteEngine.DefaultSocketPath}",
        };

        if (remote.KeyFile is { Length: > 0 } keyFile)
        {
            arguments.Add("-i");
            arguments.Add(keyFile);

            // Without this ssh offers every key the agent holds first, and a host with a low
            // MaxAuthTries can refuse the connection before the chosen key is ever tried. Someone who
            // names a key means that key.
            arguments.Add("-o");
            arguments.Add("IdentitiesOnly=yes");
        }

        if (remote.UsePassword)
        {
            // Password only. Otherwise ssh spends its attempts on agent keys that were not chosen, and
            // on a host with a low MaxAuthTries never reaches the password at all.
            arguments.Add("-o");
            arguments.Add("PreferredAuthentications=password,keyboard-interactive");
        }

        if (remote.Port is { } port)
        {
            arguments.Add("-p");
            arguments.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        arguments.Add(remote.User is { Length: > 0 } user ? $"{user}@{remote.Host}" : remote.Host);
        return arguments;
    }

    private static async Task WaitForReadyAsync(
        SshForward forward, Process process, RemoteEngine remote, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (await IsListeningAsync(forward, ct).ConfigureAwait(false))
                return;

            if (process.HasExited)
            {
                // ssh's own message is the useful one — it names the reason.
                var error = (await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false)).Trim();

                // Except for one reason, where it is not (KON-260). "Host key verification failed" is
                // ssh telling a person to go and connect by hand; with BatchMode=yes it is the answer
                // for every host nobody has connected to yet, and passing it through leaves the user
                // holding a terminal instruction inside a desktop app.
                if (SshHostKeys.Classify(error) is var problem && problem != SshHostKeyProblem.None)
                    throw SshHostKeys.Failure(problem, remote, error);

                throw new InvalidOperationException(
                    error.Length > 0 ? error : $"ssh exited with code {process.ExitCode}.");
            }

            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        throw new TimeoutException(
            "The SSH tunnel did not open in time. The host may be unreachable, or the remote socket path "
            + "may be wrong.");
    }

    /// <summary>
    /// Whether the local end is accepting connections yet. A file on disk for a socket forward, and an
    /// actual connect for a port — "something is bound here" is the only honest test of a port, and it
    /// is the same claim the file's existence makes.
    /// </summary>
    private static async Task<bool> IsListeningAsync(SshForward forward, CancellationToken ct)
    {
        if (forward.SocketPath is { } path)
            return File.Exists(path);

        try
        {
            using var probe = new System.Net.Sockets.TcpClient();
            await probe.ConnectAsync(System.Net.IPAddress.Loopback, forward.Endpoint.Port, ct)
                .ConfigureAwait(false);

            return probe.Connected;
        }
        catch (Exception) when (ct.IsCancellationRequested is false)
        {
            // Nothing there yet. ssh binds the listener once authentication completes, so this is the
            // ordinary state for the first moment of every connection.
            return false;
        }
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
            // A port forward has no such leftover: killing the process is all the cleanup there is.
            if (_forward.SocketPath is { } path && File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
            // Untidy, not harmful: the next open deletes it before binding.
        }
    }
}
