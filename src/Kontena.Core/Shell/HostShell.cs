namespace Kontena.Core.Shell;

/// <summary>The shell families Kontena knows how to hand an alias to.</summary>
public enum ShellFamily
{
    /// <summary>Not one we recognise — run it, but do not try to configure it.</summary>
    Unknown,
    Bash,

    /// <summary>POSIX <c>sh</c> — dash, ash, or bash pretending. Configured through <c>$ENV</c>.</summary>
    Sh,
    Zsh,
    Fish,
    PowerShell,
    Cmd,
}

/// <summary>
/// Picks the user's shell and works out how to give it a <c>k</c> alias for <c>kubectl</c> (KON-171).
/// <para>
/// Because Kontena starts the process itself, this is not detection work but argument work: every
/// shell has a documented way to run one extra line at startup. What each of them also needs is to
/// keep the user's own configuration — an rcfile that replaces <c>~/.bashrc</c> instead of sourcing it
/// costs the user their prompt, their aliases and their PATH edits, for the sake of one alias.
/// </para>
/// <para>
/// An unrecognised shell gets no alias rather than a guessed one. A wrong startup flag is not a
/// missing feature, it is a shell that fails to open.
/// </para>
/// </summary>
public static class HostShell
{
    /// <summary>The alias every plan installs, spelled per shell.</summary>
    private const string Alias = "k";
    private const string AliasTarget = "kubectl";

    /// <summary>
    /// Turn line feeds into new lines, run by the shell itself as the first thing it does.
    /// <para>
    /// The PTY comes up with output post-processing off, so a bare line feed moves the cursor down
    /// without returning it to the left and every line starts where the last one ended. Setting it from
    /// outside the shell does not hold: a shell copies the terminal's settings while it starts and puts
    /// that copy back before running each command, so it undoes anything changed behind its back. Asking
    /// the shell to do it cannot lose that race, because it happens inside its own startup.
    /// </para>
    /// <para>Silenced, because a shell whose stty is missing should still open.</para>
    /// </summary>
    private const string FixNewlines = "stty opost onlcr 2>/dev/null";

    /// <summary>
    /// The shell to start: <c>$SHELL</c> on Unix, PowerShell or <c>ComSpec</c> on Windows. Falls back to
    /// <c>/bin/sh</c> and <c>cmd.exe</c> respectively, which exist by definition.
    /// </summary>
    public static string Detect(Func<string, string?>? environment = null)
    {
        var read = environment ?? Environment.GetEnvironmentVariable;

        if (!OperatingSystem.IsWindows())
            return read("SHELL") is { Length: > 0 } shell ? shell : "/bin/sh";

        // pwsh is the one worth preferring: it is the shell someone installed on purpose, where
        // ComSpec is only ever cmd.exe.
        if (read("KONTENA_SHELL") is { Length: > 0 } configured)
            return configured;

        return read("ComSpec") is { Length: > 0 } comspec ? comspec : "cmd.exe";
    }

    /// <summary>
    /// Which family an executable path belongs to, by file name. Both separators are cut regardless of
    /// the platform running: <c>Path</c> only splits on the host's own, and which shell a path names is
    /// not a fact about the machine reading it.
    /// </summary>
    public static ShellFamily FamilyOf(string executable)
    {
        var leaf = executable.AsSpan()[(executable.LastIndexOfAny(['/', '\\']) + 1)..];
        var name = Path.GetFileNameWithoutExtension(leaf).ToString().ToLowerInvariant();

        return name switch
        {
            "bash" => ShellFamily.Bash,
            // Not bash: on Debian and friends /bin/sh is dash, which has no --rcfile and would refuse to
            // start. POSIX sh reads the file named by $ENV instead.
            "sh" or "dash" or "ash" => ShellFamily.Sh,
            "zsh" => ShellFamily.Zsh,
            "fish" => ShellFamily.Fish,
            "pwsh" or "powershell" => ShellFamily.PowerShell,
            "cmd" => ShellFamily.Cmd,
            _ => ShellFamily.Unknown,
        };
    }

    /// <summary>
    /// How to start <paramref name="executable"/> so that <c>k</c> means <c>kubectl</c> and the given
    /// environment is in place, writing any support files into <paramref name="supportDirectory"/>.
    /// </summary>
    /// <param name="environment">
    /// Variables the session needs regardless of shell — <c>KUBECONFIG</c> above all. Copied into the
    /// plan, and added to by the shells that configure themselves through the environment.
    /// </param>
    /// <param name="readEnvironment">Reads the current environment; injectable so tests need none.</param>
    public static ShellPlan Plan(
        string executable,
        string supportDirectory,
        IReadOnlyDictionary<string, string> environment,
        Func<string, string?>? readEnvironment = null)
    {
        var read = readEnvironment ?? Environment.GetEnvironmentVariable;
        var env = new Dictionary<string, string>(environment, StringComparer.Ordinal);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);

        switch (FamilyOf(executable))
        {
            case ShellFamily.Bash:
                // --rcfile replaces ~/.bashrc rather than adding to it, so the file we point at sources
                // the user's own first. -i because an rcfile is only read by an interactive shell.
                files["kontena.bashrc"] = string.Join('\n',
                    "# Written by Kontena for this terminal session only.",
                    FixNewlines,
                    "[ -f \"$HOME/.bashrc\" ] && . \"$HOME/.bashrc\"",
                    $"alias {Alias}={AliasTarget}",
                    "");
                return new ShellPlan(
                    executable,
                    ["--rcfile", Path.Combine(supportDirectory, "kontena.bashrc"), "-i"],
                    env,
                    files);

            case ShellFamily.Sh:
                // POSIX sh has no --rcfile; an interactive one reads whatever $ENV names.
                files["kontena.shrc"] = string.Join('\n',
                    "# Written by Kontena for this terminal session only.",
                    FixNewlines,
                    $"alias {Alias}={AliasTarget}",
                    "");
                env["ENV"] = Path.Combine(supportDirectory, "kontena.shrc");
                return new ShellPlan(executable, ["-i"], env, files);

            case ShellFamily.Zsh:
                // zsh has no --rcfile: it reads .zshrc from ZDOTDIR. Point ZDOTDIR here and hand the
                // original along, because $HOME is not where everyone keeps it.
                var originalZdotdir = read("ZDOTDIR") is { Length: > 0 } zdotdir ? zdotdir : "$HOME";
                files[".zshrc"] = string.Join('\n',
                    "# Written by Kontena for this terminal session only.",
                    FixNewlines,
                    $"[ -f \"{originalZdotdir}/.zshrc\" ] && . \"{originalZdotdir}/.zshrc\"",
                    $"alias {Alias}={AliasTarget}",
                    "");
                env["ZDOTDIR"] = supportDirectory;
                return new ShellPlan(executable, ["-i"], env, files);

            case ShellFamily.Fish:
                // fish runs -C after its own config, so nothing of the user's is lost.
                return new ShellPlan(
                    executable, ["-C", $"{FixNewlines}; alias {Alias} {AliasTarget}", "-i"], env, files);

            case ShellFamily.PowerShell:
                return new ShellPlan(
                    executable,
                    ["-NoExit", "-NoLogo", "-Command", $"Set-Alias {Alias} {AliasTarget}"],
                    env,
                    files);

            case ShellFamily.Cmd:
                // doskey is cmd's only alias mechanism; $* forwards the arguments.
                return new ShellPlan(
                    executable, ["/K", $"doskey {Alias}={AliasTarget} $*"], env, files);

            default:
                // No alias rather than a guessed flag: the cost of being wrong here is a shell that
                // does not open at all.
                return new ShellPlan(executable, [], env, files);
        }
    }
}
