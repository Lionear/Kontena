using System.Diagnostics;
using Kontena.Sdk.Orchestration.Provisioning;

namespace Kontena.Core.Orchestration.Preflight;

/// <summary>
/// Runs the preflight commands over SSH, for the distributions that have a shell — kubeadm and k0s
/// (KON-234's <see cref="SshCredentials"/>).
/// <para>
/// Deliberately thin. Everything worth testing lives in the checks, which read a
/// <see cref="ProbeResult"/> and never know how it was produced; this only has to turn "run this" into
/// a process and back, and to never throw where a check expects an answer.
/// </para>
/// </summary>
public sealed class SshPreflightProbe : IPreflightProbe
{
    private readonly RemoteClusterHost _host;
    private readonly SshCredentials _credentials;
    private readonly TimeSpan _timeout;

    /// <param name="host">The machine, whose own user and key win over the cluster's (KON-233).</param>
    /// <param name="credentials">The cluster's credentials, as a fallback for what the host leaves out.</param>
    /// <param name="timeout">How long one command may take. Short: these are all near-instant.</param>
    public SshPreflightProbe(RemoteClusterHost host, SshCredentials credentials, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(credentials);

        _host = host;
        _credentials = credentials.For(host);
        _timeout = timeout ?? TimeSpan.FromSeconds(20);
    }

    public string Target => _host.Address;

    public async ValueTask<ProbeResult> RunAsync(string command, CancellationToken ct = default)
    {
        // The credentials' own rule, before anything is started: a user or key path that ssh would read
        // as one of its own options is refused here rather than handed to a process (KON-181, KON-234).
        if (_credentials.Problem() is { } problem)
            return ProbeResult.Unreachable(problem);

        var start = new ProcessStartInfo("ssh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,

            // A GUI app with no console of its own gets one made for it, which would flash a window per
            // command — and a preflight runs a handful per machine.
            CreateNoWindow = true,
        };

        foreach (var argument in Arguments(command))
            start.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(start);
            if (process is null)
                return ProbeResult.Unreachable("ssh could not be started.");

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(_timeout);

            var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderr = process.StandardError.ReadToEndAsync(deadline.Token);

            await process.WaitForExitAsync(deadline.Token);

            var output = await stdout;
            var error = await stderr;

            // ssh's own failures come back as 255, which is not the command's exit code and must not be
            // read as one: "swapon exited 255" would be reported as "swap is on".
            return process.ExitCode == 255
                ? ProbeResult.Unreachable(FirstLine(error) ?? "ssh could not connect.")
                : ProbeResult.Exit(process.ExitCode, output);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ProbeResult.Unreachable($"No answer within {_timeout.TotalSeconds:F0}s.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No ssh on this machine, or it would not start. An answer, like everything else here.
            return ProbeResult.Unreachable($"Could not run ssh: {ex.Message}");
        }
    }

    /// <summary>The argument list, kept separate so it can be read in a test without running anything.</summary>
    internal IReadOnlyList<string> Arguments(string command)
    {
        var arguments = new List<string>
        {
            // No prompts, ever. A preflight is not a terminal anyone is sitting at, so a passphrase or
            // host-key question is a hang rather than a question — and the check would time out with a
            // reason that names the wrong thing.
            "-o", "BatchMode=yes",
            "-o", $"ConnectTimeout={(int)Math.Max(5, _timeout.TotalSeconds / 2)}",
        };

        if (_credentials.KeyPath is { Length: > 0 } key)
        {
            arguments.Add("-i");
            arguments.Add(key);

            // With an explicit key, stop ssh from also offering every key the agent holds: on a machine
            // with several, the server can refuse for too many attempts before reaching the right one.
            arguments.Add("-o");
            arguments.Add("IdentitiesOnly=yes");
        }

        arguments.Add(_credentials.User is { Length: > 0 } user ? $"{user}@{_host.Address}" : _host.Address);

        // "--" first: everything after it is the remote command, so a command that starts with a hyphen
        // cannot be read as another ssh option.
        arguments.Add("--");
        arguments.Add(command);

        return arguments;
    }

    private static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
}
