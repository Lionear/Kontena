namespace Kontena.Core.Tooling;

/// <summary>Which package managers are actually present on this machine.</summary>
public static class PackageManagers
{
    private static readonly (PackageManager Manager, string Executable)[] Candidates =
    [
        (PackageManager.Homebrew, "brew"),
        (PackageManager.Winget, "winget"),
        (PackageManager.Scoop, "scoop"),
        (PackageManager.Apt, "apt-get"),
        (PackageManager.Dnf, "dnf"),
        (PackageManager.Pacman, "pacman"),
    ];

    /// <summary>
    /// The managers found here, most-preferred first. Homebrew comes before the system manager on
    /// purpose: on macOS it is the only one, and on Linux someone who installed it has already
    /// decided that is where their user-level tools live.
    /// </summary>
    public static IReadOnlyList<PackageManager> Detect()
        => [.. Candidates.Where(c => ToolLocator.Locate(c.Executable) is not null).Select(c => c.Manager)];

    /// <summary>
    /// The hint to show for a tool on this machine: the first one whose manager is installed, or the
    /// manual instructions when none of them can help.
    /// </summary>
    public static InstallHint? Best(ExternalTool tool)
    {
        var available = Detect();

        foreach (var manager in available)
        {
            var hint = tool.InstallHints.FirstOrDefault(h => h.Manager == manager);
            if (hint is not null)
                return hint;
        }

        return tool.InstallHints.FirstOrDefault(h => h.Manager == PackageManager.Manual);
    }
}
