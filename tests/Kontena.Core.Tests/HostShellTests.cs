using Kontena.Core.Shell;

namespace Kontena.Core.Tests;

/// <summary>
/// Which shell gets started and how it is told that <c>k</c> means <c>kubectl</c> (KON-171).
/// <para>
/// All of it is asserted without spawning anything, which is the reason <see cref="ShellPlan"/> is data
/// rather than a launch call: the part that can be wrong per platform is the part a test can reach.
/// </para>
/// </summary>
public sealed class HostShellTests
{
    private static readonly Dictionary<string, string> NoEnvironment = [];

    [Theory]
    [InlineData("/bin/bash", ShellFamily.Bash)]
    [InlineData("/usr/bin/zsh", ShellFamily.Zsh)]
    [InlineData("/usr/local/bin/fish", ShellFamily.Fish)]
    [InlineData("/bin/sh", ShellFamily.Sh)]
    [InlineData("/bin/dash", ShellFamily.Sh)]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe", ShellFamily.PowerShell)]
    [InlineData(@"C:\Windows\System32\cmd.exe", ShellFamily.Cmd)]
    [InlineData("/usr/bin/nushell", ShellFamily.Unknown)]
    public void A_shell_is_recognised_by_its_file_name(string executable, ShellFamily expected) =>
        Assert.Equal(expected, HostShell.FamilyOf(executable));

    [Fact]
    public void Bash_keeps_the_users_own_rcfile()
    {
        var plan = HostShell.Plan("/bin/bash", "/tmp/session", NoEnvironment, _ => null);

        // --rcfile replaces ~/.bashrc rather than adding to it, so ours has to source it: without this
        // line the terminal costs the user their prompt, aliases and PATH edits for one alias.
        Assert.Contains(".bashrc", plan.SupportFiles["kontena.bashrc"], StringComparison.Ordinal);
        Assert.Contains("alias k=kubectl", plan.SupportFiles["kontena.bashrc"], StringComparison.Ordinal);
        Assert.Equal(["--rcfile", Path.Combine("/tmp/session", "kontena.bashrc"), "-i"], plan.Arguments);
    }

    [Fact]
    public void Zsh_is_pointed_at_our_zdotdir_and_told_where_its_own_lives()
    {
        var plan = HostShell.Plan("/usr/bin/zsh", "/tmp/session", NoEnvironment,
            name => name == "ZDOTDIR" ? "/home/rick/.config/zsh" : null);

        Assert.Equal("/tmp/session", plan.Environment["ZDOTDIR"]);
        Assert.Contains("/home/rick/.config/zsh/.zshrc", plan.SupportFiles[".zshrc"], StringComparison.Ordinal);
    }

    [Fact]
    public void Zsh_without_a_zdotdir_falls_back_to_the_home_directory()
    {
        var plan = HostShell.Plan("/usr/bin/zsh", "/tmp/session", NoEnvironment, _ => null);

        Assert.Contains("$HOME/.zshrc", plan.SupportFiles[".zshrc"], StringComparison.Ordinal);
    }

    [Fact]
    public void Fish_and_powershell_and_cmd_each_get_their_own_alias_flag()
    {
        Assert.Contains(
            HostShell.Plan("/usr/bin/fish", "/tmp/s", NoEnvironment, _ => null).Arguments,
            a => a.Contains("alias k kubectl", StringComparison.Ordinal));
        Assert.Contains("Set-Alias k kubectl", HostShell.Plan("pwsh", "/tmp/s", NoEnvironment, _ => null).Arguments);
        Assert.Contains("doskey k=kubectl $*", HostShell.Plan("cmd.exe", "/tmp/s", NoEnvironment, _ => null).Arguments);
    }

    /// <summary>
    /// A shell we do not know gets started plainly. Guessing a startup flag does not cost us the alias,
    /// it costs the user a terminal that refuses to open.
    /// </summary>
    [Fact]
    public void An_unknown_shell_is_started_without_arguments()
    {
        var plan = HostShell.Plan("/usr/bin/nushell", "/tmp/session", NoEnvironment, _ => null);

        Assert.Empty(plan.Arguments);
        Assert.Empty(plan.SupportFiles);
    }

    /// <summary>
    /// KUBECONFIG is the whole point of the session, so it survives every branch — including the
    /// unknown-shell one, where the alias is given up but the cluster is not.
    /// </summary>
    [Theory]
    [InlineData("/bin/bash")]
    [InlineData("/usr/bin/zsh")]
    [InlineData("/usr/bin/fish")]
    [InlineData("pwsh")]
    [InlineData("cmd.exe")]
    [InlineData("/usr/bin/nushell")]
    public void Every_plan_carries_the_environment_it_was_given(string executable)
    {
        var plan = HostShell.Plan(
            executable, "/tmp/session",
            new Dictionary<string, string> { ["KUBECONFIG"] = "/tmp/session/kubeconfig.yaml" },
            _ => null);

        Assert.Equal("/tmp/session/kubeconfig.yaml", plan.Environment["KUBECONFIG"]);
    }

    /// <summary>
    /// Every POSIX shell fixes the terminal's newline handling itself, in its own startup. The PTY comes
    /// up with output post-processing off, and setting it from outside cannot be made to stick: a shell
    /// copies the terminal's settings while it starts and restores that copy before each command, so it
    /// undoes anything changed behind its back. Miss this and every line of output starts where the last
    /// one ended.
    /// </summary>
    [Theory]
    [InlineData("/bin/bash", "kontena.bashrc")]
    [InlineData("/bin/sh", "kontena.shrc")]
    [InlineData("/usr/bin/zsh", ".zshrc")]
    public void A_posix_shell_repairs_the_terminals_newlines_on_startup(string executable, string file)
    {
        var plan = HostShell.Plan(executable, "/tmp/session", NoEnvironment, _ => null);

        Assert.Contains("stty opost onlcr", plan.SupportFiles[file], StringComparison.Ordinal);
    }

    [Fact]
    public void Fish_repairs_the_terminals_newlines_too() =>
        Assert.Contains(
            HostShell.Plan("/usr/bin/fish", "/tmp/s", NoEnvironment, _ => null).Arguments,
            a => a.Contains("stty opost onlcr", StringComparison.Ordinal));

    /// <summary>
    /// POSIX <c>sh</c> is not bash. On Debian it is dash, which has no <c>--rcfile</c> and would refuse
    /// to start at all; an interactive one reads the file named by <c>$ENV</c> instead.
    /// </summary>
    [Fact]
    public void Sh_is_configured_through_ENV_rather_than_an_rcfile_flag()
    {
        var plan = HostShell.Plan("/bin/sh", "/tmp/session", NoEnvironment, _ => null);

        Assert.Equal(Path.Combine("/tmp/session", "kontena.shrc"), plan.Environment["ENV"]);
        Assert.DoesNotContain("--rcfile", plan.Arguments);
        Assert.Contains("alias k=kubectl", plan.SupportFiles["kontena.shrc"], StringComparison.Ordinal);
    }

    /// <summary>
    /// The PATH a login shell would have built, for a session started from an app that inherited
    /// launchd's bare one (KON-423). Asserted against <c>/etc/paths</c> itself rather than a list
    /// spelled out here: what a login shell gets is whatever that file says, per machine.
    /// </summary>
    [Fact]
    public void On_macos_a_plan_carries_the_path_a_login_shell_would_have_built()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        // What launchd hands a GUI app, which is where this bug starts.
        var plan = HostShell.Plan(
            "/bin/zsh", "/tmp/session", NoEnvironment,
            name => name == "PATH" ? "/usr/bin:/bin:/usr/sbin:/sbin" : null);

        var directories = plan.Environment["PATH"].Split(':');

        foreach (var expected in File.ReadAllLines("/etc/paths").Where(l => l.Trim().Length > 0))
            Assert.Contains(expected.Trim(), directories);

        // Not in /etc/paths.d on an Apple Silicon install: it arrives through ~/.zprofile instead.
        Assert.Contains("/opt/homebrew/bin", directories);

        // Whatever the session already had keeps its order — an override the user arranged is one
        // they meant, and a login shell's own directories are additions, not a replacement.
        Assert.Equal("/usr/bin", directories[0]);
    }

    /// <summary>
    /// Every family gets the PATH, including the unknown one that is started with no arguments at all.
    /// That is the reason it goes in the environment rather than into each generated rcfile.
    /// </summary>
    [Theory]
    [InlineData("/bin/bash")]
    [InlineData("/bin/sh")]
    [InlineData("/usr/bin/zsh")]
    [InlineData("/usr/bin/fish")]
    [InlineData("/usr/local/bin/pwsh")]
    [InlineData("/usr/bin/nushell")]
    public void On_macos_every_family_gets_it(string executable)
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var plan = HostShell.Plan(executable, "/tmp/session", NoEnvironment, _ => null);

        Assert.Contains("/opt/homebrew/bin", plan.Environment["PATH"].Split(':'));
    }

    /// <summary>
    /// Elsewhere PATH is left alone: a Linux or Windows desktop session inherits one that was already
    /// built for it, and rewriting it would be this fix causing the problem it was meant to solve.
    /// </summary>
    [Fact]
    public void Off_macos_the_path_is_left_as_the_session_found_it()
    {
        if (OperatingSystem.IsMacOS())
            return;

        var plan = HostShell.Plan("/bin/bash", "/tmp/session", NoEnvironment, _ => "/usr/bin:/bin");

        Assert.False(plan.Environment.ContainsKey("PATH"));
    }

    [Fact]
    public void The_shell_comes_from_the_environment()
    {
        if (OperatingSystem.IsWindows())
            return;

        Assert.Equal("/usr/bin/fish", HostShell.Detect(name => name == "SHELL" ? "/usr/bin/fish" : null));
        Assert.Equal("/bin/sh", HostShell.Detect(_ => null));
    }
}
