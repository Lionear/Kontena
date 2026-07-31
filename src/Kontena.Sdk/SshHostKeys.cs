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
    /// The host and port ssh will actually use, after <c>ssh_config</c> has had its say. Asked rather
    /// than assumed: <see cref="RemoteEngine.Host"/> may be an alias whose <c>HostName</c> and
    /// <c>Port</c> live in the user's config, and scanning the alias would fetch the wrong host's key —
    /// or no key at all.
    /// </summary>
    public static async Task<(string Host, int Port)> ResolveAsync(
        RemoteEngine remote, CancellationToken ct = default)
    {
        Guard(remote);

        var arguments = new List<string> { "-G" };
        if (remote.Port is { } configured)
        {
            arguments.Add("-p");
            arguments.Add(configured.ToString(CultureInfo.InvariantCulture));
        }

        arguments.Add(remote.User is { Length: > 0 } user ? $"{user}@{remote.Host}" : remote.Host);

        var (exitCode, stdout, _) = await RunAsync("ssh", arguments, input: null, ScanTimeout, ct)
            .ConfigureAwait(false);

        // ssh -G is a convenience, not a dependency: an older client, or one that dislikes the options,
        // should not stop the scan. Falling back to what the user typed is what would have happened
        // anyway when no alias is involved, which is the common case.
        if (exitCode != 0)
            return (remote.Host, remote.Port ?? 22);

        var host = Setting(stdout, "hostname") ?? remote.Host;
        var port = int.TryParse(Setting(stdout, "port"), CultureInfo.InvariantCulture, out var resolved)
            ? resolved
            : remote.Port ?? 22;

        return (host, port);
    }

    /// <summary>
    /// The keys a host is offering, with their fingerprints. This is trust-on-first-use: the answer is
    /// only worth as much as the network it came over, which is exactly why it is shown to a person
    /// instead of being accepted here.
    /// </summary>
    public static async Task<IReadOnlyList<SshHostKey>> ScanAsync(
        RemoteEngine remote, CancellationToken ct = default)
    {
        var (host, port) = await ResolveAsync(remote, ct).ConfigureAwait(false);

        var scan = await RunAsync(
            "ssh-keyscan",
            ["-T", "5", "-p", port.ToString(CultureInfo.InvariantCulture), host],
            input: null, ScanTimeout, ct).ConfigureAwait(false);

        var lines = scan.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#'))
            .ToList();

        if (lines.Count == 0)
        {
            // ssh-keyscan says nothing at all when it cannot reach the host — the reason is on stderr.
            var reason = scan.Stderr.Trim();
            throw new EngineException(
                reason.Length > 0
                    ? reason
                    : $"No SSH host key came back from {host}:{port.ToString(CultureInfo.InvariantCulture)}.");
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

    /// <summary>One setting out of <c>ssh -G</c>, which prints <c>name value</c> per line, lowercased.</summary>
    private static string? Setting(string output, string name)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && string.Equals(parts[0], name, StringComparison.OrdinalIgnoreCase))
                return parts[1];
        }

        return null;
    }

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
    /// line here too, and ssh-keyscan reads a leading "-" as its own option just as ssh does.
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
