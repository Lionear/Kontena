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
    [InlineData("/bin/sh", ShellFamily.Bash)]
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
        Assert.Contains("alias k kubectl", HostShell.Plan("/usr/bin/fish", "/tmp/s", NoEnvironment, _ => null).Arguments);
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

    [Fact]
    public void The_shell_comes_from_the_environment()
    {
        if (OperatingSystem.IsWindows())
            return;

        Assert.Equal("/usr/bin/fish", HostShell.Detect(name => name == "SHELL" ? "/usr/bin/fish" : null));
        Assert.Equal("/bin/sh", HostShell.Detect(_ => null));
    }
}
