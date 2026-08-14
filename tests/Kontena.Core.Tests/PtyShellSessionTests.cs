using System.Text;
using Kontena.Core.Shell;
using Kontena.Sdk.Shell;

namespace Kontena.Core.Tests;

/// <summary>
/// That a real pseudo-terminal comes up behind <c>IExecSession</c>, and that what comes back out is a
/// terminal rather than a pipe (KON-171).
/// <para>
/// The distinction is the entire reason for the PTY: with redirected pipes a shell notices its output
/// is not a terminal and stops behaving like one — no prompt, no echo, no line editing. So these check
/// the two things pipes cannot do, echo and resize, rather than merely that a process started.
/// </para>
/// <para>
/// Runs a shell for real, on whichever POSIX shell the machine has. Skipped on Windows, where the
/// ConPTY path exists but is not this machine's to verify.
/// </para>
/// </summary>
public sealed class PtyShellSessionTests
{
    private static bool Unsupported => OperatingSystem.IsWindows() || !File.Exists("/bin/sh");

    [Fact]
    public async Task A_shell_starts_and_echoes_what_is_typed_at_it()
    {
        if (Unsupported)
            return;

        var command = new PtyCommand("/bin/sh", ["-i"], new Dictionary<string, string>());
        await using var session = await PtyShellSession.StartAsync(
            command, Path.GetTempPath(), columns: 80, rows: 24);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var output = new StringBuilder();
        var reader = Task.Run(async () =>
        {
            await foreach (var chunk in session.ReadOutputAsync(cts.Token))
            {
                output.Append(Encoding.UTF8.GetString(chunk.Span));
                if (output.ToString().Contains("kontena-pty-ok", StringComparison.Ordinal))
                    return;
            }
        }, cts.Token);

        // Written so the line typed in and the line printed back are not the same string: a PTY echoes
        // input, so asserting on the literal would pass even against a shell that never ran it — which
        // is exactly how a broken argv got through the first time.
        await session.WriteAsync(Encoding.UTF8.GetBytes("echo kontena\"\"-pty-ok\n"), cts.Token);

        try
        {
            await reader.WaitAsync(TimeSpan.FromSeconds(20), CancellationToken.None);
        }
        catch (TimeoutException)
        {
            // fall through to the assert, which says what was actually seen
        }

        Assert.Contains("kontena-pty-ok", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A shell asked how wide its terminal is answers from the PTY's window size. Over pipes there is no
    /// window to ask about, so this is the resize path end to end rather than a call that returned
    /// without throwing. <c>stty</c> rather than <c>tput</c> because it reads the tty itself: tput wants
    /// <c>$TERM</c>, which a test runner has no reason to set.
    /// </summary>
    [Fact]
    public async Task The_shell_sees_the_size_the_session_was_resized_to()
    {
        if (Unsupported || !PathHas("stty"))
            return;

        var command = new PtyCommand("/bin/sh", ["-i"], new Dictionary<string, string>());
        await using var session = await PtyShellSession.StartAsync(
            command, Path.GetTempPath(), columns: 80, rows: 24);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var output = new StringBuilder();
        var reader = Task.Run(async () =>
        {
            await foreach (var chunk in session.ReadOutputAsync(cts.Token))
            {
                output.Append(Encoding.UTF8.GetString(chunk.Span));
                if (output.ToString().Contains("size=42 133", StringComparison.Ordinal))
                    return;
            }
        }, cts.Token);

        await session.ResizeAsync(133, 42, cts.Token);
        await session.WriteAsync(Encoding.UTF8.GetBytes("echo size=$(stty size)\n"), cts.Token);

        try
        {
            await reader.WaitAsync(TimeSpan.FromSeconds(20), CancellationToken.None);
        }
        catch (TimeoutException)
        {
            // fall through to the assert
        }

        Assert.Contains("size=42 133", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The terminal maps a line feed to carriage-return + line feed, which this PTY does not do on its
    /// own. Without it every line starts in the column where the previous one ended and the output walks
    /// diagonally down the screen — unreadable for anything longer than one line.
    /// <para>
    /// Driven through <see cref="HostShellLauncher"/> rather than a hand-made command, because the repair
    /// lives in the startup file the launcher writes. It has to be the shell's own doing: a shell copies
    /// the terminal's settings while it starts and restores that copy before running each command, so
    /// setting the mode from outside is a change it undoes. The command below is sent immediately, with
    /// no wait, which is exactly the case an outside-in fix loses.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_terminal_turns_a_line_feed_into_a_new_line()
    {
        if (Unsupported || !PathHas("stty"))
            return;

        await using var session = await HostShellLauncher.OpenAsync(
            new ClusterShellRequest("kind-test", null, null, null, []), columns: 80, rows: 24);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var output = new StringBuilder();
        var reader = Task.Run(async () =>
        {
            await foreach (var chunk in session.ReadOutputAsync(cts.Token))
            {
                output.Append(Encoding.UTF8.GetString(chunk.Span));
                if (output.ToString().Contains("modes=", StringComparison.Ordinal))
                    return;
            }
        }, cts.Token);

        // One flag per line, then matched whole: "onlcr" and "-onlcr" are different answers, and where
        // stty happens to wrap its output is a function of the terminal width, not of the mode.
        //
        // Written mo""des so the word the reader waits for cannot appear in the echo of the command
        // itself — a PTY echoes input, so a marker that survives the shell verbatim ends the read before
        // the answer has been printed.
        await session.WriteAsync(
            Encoding.UTF8.GetBytes("echo mo\"\"des=$(stty -a | tr ' ' '\\n' | grep -cx onlcr)\n"), cts.Token);

        try
        {
            await reader.WaitAsync(TimeSpan.FromSeconds(20), CancellationToken.None);
        }
        catch (TimeoutException)
        {
            // fall through to the assert
        }

        Assert.Contains("modes=1", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The session also sets the mode on the terminal directly, which is what a shell Kontena does not
    /// recognise has to fall back on — there is no startup file to put an <c>stty</c> in.
    /// <para>
    /// Checked with <c>cat</c> standing in for that shell: it never touches the terminal's settings, so
    /// what comes back is the mode we set rather than one a shell saved and restored. A line typed into
    /// a terminal in this mode is echoed back with the carriage return the PTY does not add by itself.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_terminal_itself_is_set_for_a_shell_that_cannot_be_configured()
    {
        if (Unsupported || !File.Exists("/bin/cat"))
            return;

        var command = new PtyCommand("/bin/cat", [], new Dictionary<string, string>());
        await using var session = await PtyShellSession.StartAsync(
            command, Path.GetTempPath(), columns: 80, rows: 24);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var output = new StringBuilder();
        var reader = Task.Run(async () =>
        {
            await foreach (var chunk in session.ReadOutputAsync(cts.Token))
            {
                output.Append(Encoding.UTF8.GetString(chunk.Span));
                if (output.ToString().Contains('\r', StringComparison.Ordinal))
                    return;
            }
        }, cts.Token);

        await session.WriteAsync(Encoding.UTF8.GetBytes("kontena\n"), cts.Token);

        try
        {
            await reader.WaitAsync(TimeSpan.FromSeconds(20), CancellationToken.None);
        }
        catch (TimeoutException)
        {
            // fall through to the assert
        }

        Assert.Contains("\r\n", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>The session directory is the session's; nothing it wrote outlives the window.</summary>
    [Fact]
    public async Task Closing_the_session_takes_its_support_directory_with_it()
    {
        if (Unsupported)
            return;

        var directory = Path.Combine(Path.GetTempPath(), $"kontena-pty-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "kubeconfig.yaml"), "current-context: \"x\"\n");

        var command = new PtyCommand("/bin/sh", ["-i"], new Dictionary<string, string>());
        var session = await PtyShellSession.StartAsync(command, Path.GetTempPath(), 80, 24, directory);

        await session.DisposeAsync();

        Assert.False(Directory.Exists(directory));
    }

    private static bool PathHas(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Any(dir => dir.Length > 0 && File.Exists(Path.Combine(dir, tool)));
}
