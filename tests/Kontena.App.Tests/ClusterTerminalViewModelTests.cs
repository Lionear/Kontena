using Kontena.App.ViewModels;
using Kontena.Core.Models;
using Kontena.App;
using Kontena.Core.Shell;

namespace Kontena.App.Tests;

/// <summary>The cluster terminal page's header and its one precondition (KON-171).</summary>
public sealed class ClusterTerminalViewModelTests
{
    private static readonly TerminalFont Font = new("JetBrains Mono", 13, Ligatures: false);

    private static ClusterTerminalViewModel For(string context, string? @namespace) =>
        new(new ClusterTerminals().Add(
                "kubernetes:" + context,
                new ClusterShellRequest(context, "kind-test", "kind-test", @namespace, ["/home/rick/.kube/config"])),
            Font);

    /// <summary>
    /// The page is the terminal, so there is no tab to wait for — unlike container and pod detail, where
    /// the session only starts once the Shell tab is shown.
    /// </summary>
    [Fact]
    public void The_page_is_the_terminal_so_it_is_always_the_selected_one() =>
        Assert.True(For("kind-test", null).IsTerminalSelected);

    [Fact]
    public void A_context_is_what_makes_the_page_openable()
    {
        Assert.True(For("kind-test", null).CanOpenTerminal);
        Assert.False(For("   ", null).CanOpenTerminal);
    }

    /// <summary>
    /// The namespace chip is hidden rather than blank when nothing was pinned. A header claiming a
    /// namespace <c>kubectl</c> will not actually use is worse than no header — the whole reason to show
    /// the environment is that you should not have to check it.
    /// </summary>
    [Fact]
    public void The_namespace_is_only_claimed_when_there_is_one()
    {
        Assert.True(For("kind-test", "argocd").HasNamespace);
        Assert.False(For("kind-test", null).HasNamespace);
        Assert.False(For("kind-test", string.Empty).HasNamespace);
    }

    /// <summary>
    /// The status line names the shell that is actually running. A container shell is always
    /// <c>/bin/sh</c>; here it is whatever the user has, so it is read rather than assumed.
    /// </summary>
    [Fact]
    public void The_status_line_names_the_users_own_shell() =>
        Assert.Equal(Path.GetFileName(HostShell.Detect()), For("kind-test", null).ShellLabel);
}
