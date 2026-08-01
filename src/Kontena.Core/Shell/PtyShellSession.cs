using System.Runtime.InteropServices;
using Kontena.Sdk.Models;
using Porta.Pty;

namespace Kontena.Core.Shell;

/// <summary>
/// A shell running on this machine, behind the same <see cref="IExecSession"/> the container and pod
/// shells use (KON-171).
/// <para>
/// That interface already had the shape of a PTY — bytes out, bytes in, resize — because a container
/// exec <em>is</em> one, provided by the engine at the far end. On the host there is nothing providing
/// it, so this is the piece that was missing: everything above it, including the terminal control and
/// its font settings, is reused untouched.
/// </para>
/// <para>
/// A real pseudo-terminal and not redirected pipes, because the difference is the whole feature. With
/// plain pipes an interactive shell sees that its output is not a terminal and stops being one: no
/// prompt, no line editing or history, no colour from the tools it runs, no <c>Ctrl-C</c> reaching the
/// foreground process, and no way to tell anyone the window resized.
/// </para>
/// </summary>
public sealed class PtyShellSession : IExecSession
{
    private readonly IPtyConnection _pty;
    private readonly string? _supportDirectory;
    private readonly SafeHandle? _terminal;
    private int _outputTaken;
    private int _settled;

    private PtyShellSession(IPtyConnection pty, string? supportDirectory, SafeHandle? terminal)
    {
        _pty = pty;
        _supportDirectory = supportDirectory;
        _terminal = terminal;
        _pty.ProcessExited += (_, e) => ExitCode = e.ExitCode;
    }

    /// <summary>
    /// Start <paramref name="plan"/> in a pseudo-terminal of the given size.
    /// </summary>
    /// <param name="supportDirectory">
    /// Removed when the session ends. Holds the generated rcfile and kubeconfig overlay, which exist
    /// only for this shell.
    /// </param>
    public static async ValueTask<PtyShellSession> StartAsync(
        ShellPlan plan,
        string workingDirectory,
        int columns,
        int rows,
        string? supportDirectory = null,
        CancellationToken ct = default)
    {
        var options = new PtyOptions
        {
            Name = "Kontena",
            App = plan.Executable,
            // Arguments only. Porta prepends the program itself, so passing argv[0] here hands the
            // shell its own binary as a script to run: "/bin/sh: cannot execute binary file", and a
            // terminal that echoes what you type without ever running it.
            CommandLine = [.. plan.Arguments],
            Cwd = workingDirectory,
            Cols = Math.Max(columns, 1),
            Rows = Math.Max(rows, 1),
            Environment = Inherited(plan.Environment),
        };

        var pty = await PtyProvider.SpawnAsync(options, ct).ConfigureAwait(false);

        // The PTY arrives with output post-processing off, which makes every line start where the last
        // one ended. Best-effort: if the handle is not reachable the shell still runs, it just looks
        // wrong, and that beats refusing to open one at all.
        var terminal = pty.WriterStream is FileStream { SafeFileHandle: { IsInvalid: false } h } ? h : null;

        if (terminal is not null)
            PosixTerminalModes.EnableOutputPostProcessing(terminal);

        return new PtyShellSession(pty, supportDirectory, terminal);
    }

    /// <inheritdoc/>
    public int? ExitCode { get; private set; }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _outputTaken, 1) == 1)
            throw new InvalidOperationException("The output of an exec session can only be read once.");

        var buffer = new byte[8192];

        while (!ct.IsCancellationRequested)
        {
            int read;

            try
            {
                read = await _pty.ReaderStream.ReadAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // The shell exiting closes the far end mid-read; that is an ending, not a fault.
                yield break;
            }

            if (read <= 0)
                yield break;

            // Again, once, as soon as the shell has said anything at all.
            //
            // A shell captures the terminal's settings while it starts up and puts that copy back every
            // time it reads a line, so a change made before it looked is a change it undoes. There is no
            // point in the handshake we can wait for, but the first byte out is proof it is up: by then
            // its copy is the one we set. Anything is welcome — a prompt, a banner, the echo of a
            // keystroke — because all of it comes from a shell that has finished starting.
            if (Interlocked.Exchange(ref _settled, 1) == 0 && _terminal is not null)
                PosixTerminalModes.EnableOutputPostProcessing(_terminal);

            yield return new ReadOnlyMemory<byte>(buffer, 0, read).ToArray();
        }
    }

    /// <inheritdoc/>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken ct = default)
    {
        await _pty.WriterStream.WriteAsync(input, ct).ConfigureAwait(false);
        await _pty.WriterStream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public ValueTask ResizeAsync(int columns, int rows, CancellationToken ct = default)
    {
        // Best-effort by contract: a resize racing the shell's exit is not worth surfacing.
        try
        {
            _pty.Resize(Math.Max(columns, 1), Math.Max(rows, 1));
        }
        catch (Exception)
        {
            // ignored
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        try
        {
            if (ExitCode is null)
                _pty.Kill();
        }
        catch (Exception)
        {
            // already gone
        }

        (_pty as IDisposable)?.Dispose();

        // The overlay names the cluster and the rcfile names the user's own files; neither should
        // outlive the window they were written for.
        if (_supportDirectory is { Length: > 0 } dir)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // a file still open on Windows; the temp directory is cleaned by the OS
            }
            catch (UnauthorizedAccessException)
            {
                // same
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The parent environment plus the session's own entries. Built explicitly rather than relying on
    /// the PTY layer to inherit, so what the shell sees is the same on all three platforms.
    /// </summary>
    private static Dictionary<string, string> Inherited(IReadOnlyDictionary<string, string> extra)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        var env = new Dictionary<string, string>(comparer);

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key is string key && entry.Value is string value)
                env[key] = value;

        foreach (var (key, value) in extra)
            env[key] = value;

        return env;
    }
}
