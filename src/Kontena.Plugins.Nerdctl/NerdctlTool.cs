using Kontena.Sdk.Tooling;

namespace Kontena.Plugins.Nerdctl;

/// <summary>
/// nerdctl described the way <see cref="KnownTools"/> describes every CLI the SDK already drives —
/// this one lives in the plugin instead of the SDK because nerdctl is the plugin's business, not the
/// host's (KON-141). It has no daemon socket to probe, so whether it is present and which version it
/// is comes from running it, the same as any other <see cref="ExternalTool"/>.
/// </summary>
public static class NerdctlTool
{
    public static readonly ExternalTool Definition = new(
        "nerdctl",
        "nerdctl",
        ["--version"],
        [
            new InstallHint(PackageManager.Homebrew, "brew", ["install", "nerdctl"]),
            new InstallHint(PackageManager.Manual, "", []),
        ])
    {
        // Only Homebrew is certain enough to name — see KnownTools' remarks on guessed packages
        // costing more than no hint. nerdctl's own releases are the honest answer everywhere else.
        DocumentationUrl = "https://github.com/containerd/nerdctl",

        // Read on Settings › Tools since KON-438, where the row needs a line saying what it is for.
        Purpose = "The nerdctl plugin's containers: without it the plugin has no backend to offer.",
    };
}
