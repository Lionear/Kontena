using Kontena.Sdk.Tooling;

namespace Kontena.Plugins.ManifestStudio.Git;

/// <summary>
/// <c>git</c> described the way <see cref="KnownTools"/> describes every CLI the host drives itself,
/// and the way <c>NerdctlTool</c> describes nerdctl — in the extension that needs it rather than in the
/// SDK's own list (KON-438).
/// <para>
/// The description lives here because the tool does: the core app never runs <c>git</c>, so an entry in
/// <see cref="KnownTools"/> would make every installation carry a row for a command only this plugin
/// uses. Declaring it on <c>ManifestStudioPlugin.Manifest</c> is what puts it on Settings &#8250; Tools
/// anyway, with the same detection, version check and install hints kubectl gets.
/// </para>
/// <para>
/// No <see cref="ExternalTool.Release"/>. Kontena only fetches tools whose publisher ships one verified
/// binary per platform; git is packaged by every distribution and installed by an installer on Windows,
/// so the honest offer is the package manager's command and the download page, not a copy of git in
/// Kontena's own folder.
/// </para>
/// </summary>
public static class GitTool
{
    public static readonly ExternalTool Definition = new(
        "git",
        "git",
        ["--version"],
        [
            new InstallHint(PackageManager.Homebrew, "brew", ["install", "git"]),
            new InstallHint(PackageManager.Winget, "winget", ["install", "-e", "--id", "Git.Git"]),
            new InstallHint(PackageManager.Scoop, "scoop", ["install", "git"]),
            new InstallHint(PackageManager.Dnf, "dnf", ["install", "git"], RequiresElevation: true),
            new InstallHint(PackageManager.Apt, "apt-get", ["install", "-y", "git"], RequiresElevation: true),
            new InstallHint(PackageManager.Pacman, "pacman", ["-S", "git"], RequiresElevation: true),
            new InstallHint(PackageManager.Manual, "", []),
        ])
    {
        DocumentationUrl = "https://git-scm.com/downloads",
        Purpose = "Manifest Studio's Source control page: status, diff, commit, push in the folder you opened.",

        // 2.23 is where `git switch` arrived, which is how SwitchBranchAsync changes branch. The rest of
        // GitCli is older than that by years.
        MinimumVersion = "2.23",

        OutdatedConsequence =
            "Manifest Studio needs git 2.23 or newer to switch branch, which is when `git switch` " +
            "arrived. On an older one that one button fails and says so; status, diff, commit and push " +
            "all work.",
    };
}
