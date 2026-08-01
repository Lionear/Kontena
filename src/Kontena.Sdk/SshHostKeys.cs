using System.Diagnostics;
using System.Globalization;
using Kontena.Sdk.Errors;
using Kontena.Sdk.Models;

namespace Kontena.Sdk;

/// <summary>One key a host offered, in both the forms this needs (KON-260).</summary>
/// <param name="KeyType">Algorithm as ssh names it, e.g. <c>ssh-ed25519</c>.</param>
/// <param name="Fingerprint">The <c>SHA256:…</c> form — what a person compares.</param>
/// <param name="KnownHostsLine">The whole scanned line — what <c>known_hosts</c> stores.</param>
public sealed record SshHostKey(string KeyType, string Fingerprint, string KnownHostsLine);

/// <summary>
/// Reading and trusting SSH host keys, so that meeting a new host is something Kontena can walk a
/// person through rather than a dead end (KON-260).
/// <para>
/// Kontena connects with <c>BatchMode=yes</c>, which means ssh may not ask anything — so a host that
/// is not yet in <c>known_hosts</c> simply fails, on every platform, until someone opens a terminal
/// and connects by hand. That is the whole reason remote engines over SSH had never worked. The fix
/// is not to stop checking (<c>StrictHostKeyChecking=accept-new</c> trusts silently at the one moment
/// the check exists for) but to show the fingerprint and let the user say yes.
/// </para>
/// </summary>
public static class SshHostKeys
{
    /// <summary>Scanning a host that is not answering should not outlast a connection attempt.</summary>
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Which of the two host key failures ssh is complaining about, if either.</summary>
    public static SshHostKeyProblem Classify(string? complaint)
    {
        if (string.IsNullOrWhiteSpace(complaint))
            return SshHostKeyProblem.None;

        // Order matters: a changed key also ends with "Host key verification failed", so the louder
        // case has to be recognised first or every changed key would be offered up for trusting.
        if (complaint.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.Ordinal))
            return SshHostKeyProblem.Changed;

        if (complaint.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase)
            || complaint.Contains("No matching host key", StringComparison.OrdinalIgnoreCase)
            || (complaint.Contains("host key is known for", StringComparison.OrdinalIgnoreCase)
                && complaint.Contains("strict checking", StringComparison.OrdinalIgnoreCase)))
        {
            return SshHostKeyProblem.Unknown;
        }

        return SshHostKeyProblem.None;
    }

    /// <summary>The exception for a classified failure, worded for the case it actually is.</summary>
    public static SshHostKeyException Failure(SshHostKeyProblem problem, RemoteEngine remote, string complaint)
        => new(
            problem,
            problem switch
            {
                SshHostKeyProblem.Changed =>
                    $"The host key for {remote.Host} is not the one Kontena trusted before. That is either a "
                    + "rebuilt machine or a connection being intercepted, and Kontena will not guess which. "
                    + "Remove the old entry yourself once you know.",
                _ =>
                    $"Kontena has not connected to {remote.Host} before, so it cannot tell whether this is the "
                    + "machine you mean. Review its fingerprint to continue.",
            },
            complaint);

    /// <summary>
    /// The key this host offers, with its fingerprint. Trust-on-first-use: the answer is only worth as
    /// much as the network it came over, which is exactly why it is shown to a person instead of being
    /// accepted here.
    /// </summary>
    /// <remarks>
    /// Fetched by <b>ssh itself</b>, writing to a throwaway <c>known_hosts</c> — not by
    /// <c>ssh-keyscan</c>, which was the first attempt and was wrong for the same reason a parser test
    /// is not a request test (KON-75). They are two clients: keyscan can fail to agree a key exchange
    /// on a host ssh connects to without trouble, and then Kontena refuses to show a fingerprint for a
    /// host it could otherwise reach. Seen in the field —
    /// <c>choose_kex: unsupported KEX method sntrup761x25519-sha512@openssh.com</c> from keyscan
    /// against an OpenSSH 9.2 host that ssh was perfectly happy with.
    /// <para>
    /// Using ssh also means <c>ssh_config</c> is honoured for free — aliases, <c>HostName</c>,
    /// <c>Port</c>, <c>ProxyJump</c> — where keyscan had to be told a resolved host and port and would
    /// have scanned the wrong machine when it was told wrong.
    /// </para>
    /// <para>
    /// It offers no credentials (<c>PreferredAuthentications=none</c>): the host key is exchanged
    /// before authentication, so there is nothing to gain from trying, and a failed login on every
    /// fingerprint check is how a host's own defences start counting.
    /// </para>
    /// </remarks>
    public static async Task<IReadOnlyList<SshHostKey>> ScanAsync(
        RemoteEngine remote, CancellationToken ct = default)
    {
        Guard(remote);

        var capture = Path.Combine(Path.GetTempPath(), $"kontena-hostkey-{Guid.NewGuid():N}");

        try
        {
            var arguments = new List<string>
            {
                "-N", "-T",
                "-o", "BatchMode=yes",
                "-o", "ConnectTimeout=10",

                // The key arrives before authentication does, so asking to authenticate would only add
                // a refused login to someone's logs.
                "-o", "PreferredAuthentications=none",

                // Into a file of ours. accept-new here is capture, not trust: nothing the user has is
                // touched, and what lands there is only written to known_hosts once they say so.
                "-o", "StrictHostKeyChecking=accept-new",

                // Quoted, because a temp path can contain spaces — C:\Users\John Doe\AppData\… — and
                // ssh then takes the value up to the space and writes the file somewhere else. It still
                // reports "Permanently added", so the failure is entirely silent.
                "-o", $"UserKnownHostsFile=\"{capture}\"",
            };

            if (remote.Port is { } port)
            {
                arguments.Add("-p");
                arguments.Add(port.ToString(CultureInfo.InvariantCulture));
            }

            arguments.Add(remote.User is { Length: > 0 } user ? $"{user}@{remote.Host}" : remote.Host);

            var attempt = await RunAsync("ssh", arguments, input: null, ScanTimeout, ct)
                .ConfigureAwait(false);

            var lines = File.Exists(capture)
                ? (await File.ReadAllLinesAsync(capture, ct).ConfigureAwait(false))
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0 && !line.StartsWith('#'))
                    .ToList()
                : [];

            if (lines.Count == 0)
            {
                // ssh never got as far as a key. Its own message names the reason — refused, timed out,
                // no route — and nothing written here would name it better.
                var reason = attempt.Stderr.Trim();
                throw new EngineException(
                    reason.Length > 0 ? reason : $"No SSH host key came back from {remote.Host}.");
            }

            // One ssh-keygen for all of them: it prints a fingerprint per input line, in order.
            var fingerprints = await RunAsync(
                "ssh-keygen", ["-l", "-f", "-"], string.Join('\n', lines) + "\n", ScanTimeout, ct)
                .ConfigureAwait(false);

            var printed = fingerprints.Stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            return lines
                .Select((line, index) => new SshHostKey(
                    KeyType: line.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 1 } parts
                        ? parts[1]
                        : "unknown",
                    Fingerprint: index < printed.Count ? Fingerprint(printed[index]) : "unavailable",
                    KnownHostsLine: line))
                .ToList();
        }
        finally
        {
            try
            {
                if (File.Exists(capture))
                    File.Delete(capture);
            }
            catch (IOException)
            {
                // A stray file in the temp directory, named after a guid. Not worth failing over.
            }
        }
    }

    /// <summary>
    /// Writes the scanned lines to <c>known_hosts</c>, which is where ssh itself would have put them.
    /// The user's file rather than one of Kontena's own: a host trusted here is then trusted from the
    /// terminal too, and there is only one place to look when it has to be undone.
    /// </summary>
    /// <param name="knownHostsPath">
    /// Where to write. Defaults to <see cref="KnownHostsPath"/>; given explicitly by the tests, which
    /// must not append to the machine's real file to find out whether appending works.
    /// </param>
    public static async Task TrustAsync(
        IEnumerable<SshHostKey> keys, string? knownHostsPath = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var lines = keys.Select(key => key.KnownHostsLine).Where(line => line.Length > 0).ToList();
        if (lines.Count == 0)
            return;

        var path = knownHostsPath ?? KnownHostsPath;
        var directory = Path.GetDirectoryName(path)!;
        var created = !Directory.Exists(directory);
        Directory.CreateDirectory(directory);

        // Only what Kontena made itself. ssh refuses to use a world-readable ~/.ssh, so a directory
        // created here has to match what ssh would have created — but a directory that was already
        // there belongs to the user, and its permissions are not ours to decide.
        if (created && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // A file whose last line has no newline would otherwise gain a line that is two entries glued
        // together — and an unparseable known_hosts line is silently ignored, so the host would stay
        // unknown with nothing to show for it.
        var separator = File.Exists(path) && new FileInfo(path).Length > 0 && !await EndsWithNewlineAsync(path, ct)
            .ConfigureAwait(false)
            ? Environment.NewLine
            : string.Empty;

        await File.AppendAllTextAsync(
            path,
            separator + string.Join(Environment.NewLine, lines) + Environment.NewLine,
            ct).ConfigureAwait(false);
    }

    /// <summary>Where ssh keeps the hosts it trusts.</summary>
    public static string KnownHostsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "known_hosts");

    /// <summary>The <c>SHA256:…</c> field out of an <c>ssh-keygen -l</c> line.</summary>
    private static string Fingerprint(string printed)
        => printed.Split(' ', StringSplitOptions.RemoveEmptyEntries)
               .FirstOrDefault(part => part.StartsWith("SHA256:", StringComparison.Ordinal))
           ?? printed;

    private static async Task<bool> EndsWithNewlineAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        stream.Seek(-1, SeekOrigin.End);

        var last = new byte[1];
        await stream.ReadExactlyAsync(last, ct).ConfigureAwait(false);
        return last[0] is (byte)'\n';
    }

    /// <summary>
    /// The same gate <see cref="SshTunnel.Arguments"/> uses (KON-181): these values reach a command
    /// line here too, and the scan runs ssh with the same host and user the tunnel will use.
    /// </summary>
    private static void Guard(RemoteEngine remote)
    {
        ArgumentNullException.ThrowIfNull(remote);

        if (RemoteEngine.ArgumentProblem(remote.Host, remote.User, remote.SocketPath) is { } problem)
            throw new ArgumentException(problem, nameof(remote));
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string executable, IReadOnlyList<string> arguments, string? input, TimeSpan timeout, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
            ?? throw new EngineException($"Could not start {executable}. Is an SSH client installed?");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);

        if (input is not null)
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), deadline.Token).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Kill(process);
            throw new EngineException($"{executable} did not answer in time.");
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Gone between the check and the kill.
        }
    }
}
