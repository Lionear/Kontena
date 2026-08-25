using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Kontena.Sdk;

namespace Kontena.App.Services;

/// <summary>
/// The on-disk half of <see cref="Diag"/>: what the app did, and how much memory it was holding while
/// it did it, kept where it can still be read after the run that went wrong (KON-389).
/// <para>
/// It exists because of KON-388 — a crash nobody could reproduce, from a session nobody could
/// reconstruct. <c>KONTENA_TRACE=1</c> already produced exactly the right material, and produced it
/// where a user could not get at it: an environment variable set before launch, printed to a stderr
/// that a desktop app has no console for. This is the same trace with the two ends changed — switched
/// on from Settings, written to a file — which is why it is a sink under <see cref="Diag"/> rather
/// than a second mechanism beside it. One set of marks, one place they are worded, two ways out.
/// </para>
/// <para>
/// One generation is kept, not a rolling set: the log that matters is the one from the session that
/// crashed, and the session after it is the one where you go looking. So opening rotates whatever is
/// there to <c>.prev</c> and starts clean — a crash and the launch that follows it are always both on
/// disk, and a log directory can never grow without someone deciding it should.
/// </para>
/// <para>
/// <b>What must never reach this file.</b> Credentials, tokens, kubeconfig or manifest content, secret
/// and ConfigMap values, environment variables, command lines, and exception messages. The first
/// guarantee is structural: nothing here takes a payload. <see cref="Diag.Mark"/> and
/// <see cref="Diag.Action"/> take a label written in this repository, resource names and ids, and
/// numbers — the callers that hold secret material never call them, and there is no overload that
/// would let them. <see cref="Redact"/> is the second line, not the first: it catches a secret that
/// arrives inside a name or an error string we did not write, which is the case a rule about intent
/// cannot cover.
/// </para>
/// </summary>
public static partial class DiagLog
{
    /// <summary>
    /// How often the memory line is written. Half a minute is coarse on purpose: this is not a
    /// profiler, it is the shape of a leak seen across a session long enough to end in a crash.
    /// </summary>
    private static readonly TimeSpan MemoryInterval = TimeSpan.FromSeconds(30);

    /// <summary>How long a single line may be. A log nobody can scroll answers no questions.</summary>
    private const int MaxLine = 400;

    private static readonly Lock Gate = new();
    private static StreamWriter? _writer;
    private static Timer? _memory;
    private static volatile bool _open;

    /// <summary>Whether the log is being written — read on every mark, so it is the cheap check.</summary>
    public static bool IsOpen => _open;

    /// <summary>Where the log lives, beside the settings file it is switched on from.</summary>
    public static string DefaultPath { get; } = Path.Combine(ProductInfo.DataDirectory, "diagnostics.log");

    /// <summary>What the previous session's log is renamed to.</summary>
    public const string PreviousSuffix = ".prev";

    /// <summary>
    /// Start writing, archiving an existing log to <c>.prev</c> first. Does nothing if already open,
    /// so the rotation happens once per session rather than once per call.
    /// </summary>
    /// <param name="path">Where to write. For tests, which must not touch the real log.</param>
    public static void Open(string? path = null)
    {
        var target = path ?? DefaultPath;

        lock (Gate)
        {
            if (_open)
                return;

            try
            {
                var directory = Path.GetDirectoryName(target)!;
                var created = !Directory.Exists(directory);
                Directory.CreateDirectory(directory);
                if (created)
                    RestrictToOwner(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

                if (File.Exists(target))
                    File.Move(target, target + PreviousSuffix, overwrite: true);

                // Mode before content, as SettingsStore does: the lines below are never briefly
                // world-readable on a machine with other accounts on it.
                File.Create(target).Dispose();
                RestrictToOwner(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);

                _writer = new StreamWriter(target, append: true) { AutoFlush = true };
                _open = true;
            }
            catch
            {
                // A diagnostic must never be the reason the app fails to start. An unwritable log is
                // no log, and the app carries on exactly as if the setting were off.
                _writer?.Dispose();
                _writer = null;
                _open = false;
                return;
            }
        }

        // Flushed on every line rather than buffered, because the run this is for does not get to
        // shut down cleanly — a buffer is precisely the part that would be lost.
        Write($"session start — Kontena {AppVersion.Current} on {RuntimeInformation.OSDescription} "
            + $"({RuntimeInformation.OSArchitecture})");
        WriteMemory();

        _memory = new Timer(_ => WriteMemory(), null, MemoryInterval, MemoryInterval);
    }

    /// <summary>Stop writing and release the file. The next <see cref="Open"/> rotates again.</summary>
    public static void Close()
    {
        Timer? timer;

        lock (Gate)
        {
            _open = false;
            timer = _memory;
            _memory = null;
            _writer?.Dispose();
            _writer = null;
        }

        timer?.Dispose();
    }

    /// <summary>Append one line, stamped with the wall clock and passed through <see cref="Redact"/>.</summary>
    /// <remarks>
    /// The wall clock rather than <see cref="Diag"/>'s milliseconds-since-start: a file read days later
    /// is matched against when the user says it happened, and against everything else on the machine
    /// that has a timestamp.
    /// </remarks>
    internal static void Write(string line)
    {
        if (!_open)
            return;

        var safe = Redact(line);
        if (safe.Length > MaxLine)
            safe = string.Concat(safe.AsSpan(0, MaxLine), "…");

        lock (Gate)
        {
            if (_writer is null)
                return;

            try
            {
                _writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {safe}");
            }
            catch
            {
                // Full disk, removed drive, revoked permission. Losing the diagnostic is the whole
                // cost of this going wrong; taking the app with it would not be.
            }
        }
    }

    private const long Mib = 1024 * 1024;

    /// <summary>
    /// One memory sample. Working set and managed heap together, because they answer different halves
    /// of the same question: a managed leak grows both, and a native or graphics leak grows only the
    /// first — which is the shape a crash on a long session usually turns out to have.
    /// </summary>
    private static void WriteMemory() =>
        Write($"memory — working set {Environment.WorkingSet / Mib} MB, managed {GC.GetTotalMemory(false) / Mib} MB, "
            + $"gen2 collections {GC.CollectionCount(2)}");

    /// <summary>
    /// Strip anything that looks like a credential from a line before it is written.
    /// <para>
    /// A backstop, deliberately blunt. The rule that keeps secrets out of this file is that nothing
    /// puts them in — see the type's remarks — and this is what covers the case that rule cannot: a
    /// token inside a resource name, a password in a URL an engine handed back, an error string
    /// written somewhere else entirely. It over-redacts rather than under-redacts, because a line
    /// that lost a word is a worse log and a line that kept a token is an incident.
    /// </para>
    /// <para>
    /// It cannot recognise a secret that looks like ordinary text, and does not try: a long lowercase
    /// hex run is left alone because that is what every container and image id in this file is.
    /// </para>
    /// </summary>
    internal static string Redact(string line)
    {
        line = UrlCredentials().Replace(line, "$1//***:***@");

        // Before the assignment rule, which would otherwise stop at the scheme word and leave the
        // token after it standing: "Authorization: Bearer <token>" is two words, not one value.
        line = AuthScheme().Replace(line, "$1 ***");
        line = SecretAssignment().Replace(line, "$1=***");
        line = JsonWebToken().Replace(line, "***");
        return OpaqueToken().Replace(line, "***");
    }

    /// <summary>A user and password carried in a URL: <c>https://user:pass@host</c>.</summary>
    [GeneratedRegex(@"([A-Za-z][A-Za-z0-9+.\-]*:)//[^/\s:@]+:[^/\s@]+@")]
    private static partial Regex UrlCredentials();

    /// <summary>An HTTP authorization scheme and the credential that follows it.</summary>
    [GeneratedRegex(@"\b(Bearer|Basic)\s+\S+", RegexOptions.IgnoreCase)]
    private static partial Regex AuthScheme();

    /// <summary>A secret-shaped name with a value after it, however it is spelled or separated.</summary>
    [GeneratedRegex(
        @"\b(pass(?:word|phrase)?|secret|token|apikey|api[_-]?key|credentials?|authorization|auth|bearer)\b\s*[=:]\s*\S+",
        RegexOptions.IgnoreCase)]
    private static partial Regex SecretAssignment();

    /// <summary>A JWT — a service-account or OIDC token, which is a credential wherever it turns up.</summary>
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)?")]
    private static partial Regex JsonWebToken();

    /// <summary>
    /// A long base64-shaped run. Required to mix cases and digits so that the ids this log exists to
    /// record — 64 lowercase hex characters, every one of them — are not mistaken for key material.
    /// </summary>
    [GeneratedRegex(@"(?=[A-Za-z0-9+/]*[A-Z])(?=[A-Za-z0-9+/]*[a-z])(?=[A-Za-z0-9+/]*[0-9])\b[A-Za-z0-9+/]{32,}={0,2}")]
    private static partial Regex OpaqueToken();

    /// <summary>
    /// Keep <paramref name="path"/> to its owner on Unix, the way <see cref="SettingsStore"/> does for
    /// the settings file next to it. Windows inherits the user profile's ACL and needs nothing.
    /// </summary>
    private static void RestrictToOwner(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(path, mode);
        }
        catch
        {
            // A filesystem without Unix modes (a mounted share) refuses this. The log is still
            // written; it is only not narrowed.
        }
    }
}
