using Kontena.Sdk.Tooling;

namespace Kontena.Adapters.Apple;

/// <summary>
/// Apple's <c>container</c> CLI described the way <see cref="KnownTools"/> describes every other CLI
/// Kontena drives. It lives here rather than in the SDK for the same reason <c>NerdctlTool</c> does:
/// it is this adapter's business, not the host's.
/// <para>
/// Deliberately <b>not</b> a managed tool. Everything in <see cref="KnownTools"/> that carries a
/// <see cref="ExternalTool.Release"/> can be downloaded and updated by Kontena itself; <c>container</c>
/// ships as a signed macOS installer package that needs administrator rights and installs a launchd
/// service, which is not something to do behind a progress bar. Homebrew has no cask for it either
/// (checked against 1.2.2), so the only honest hint is Apple's own release page.
/// </para>
/// </summary>
public static class AppleTool
{
    public static readonly ExternalTool Definition = new(
        "Apple container",
        "container",
        ["--version"],
        [
            new InstallHint(PackageManager.Manual, "", []),
        ])
    {
        DocumentationUrl = "https://github.com/apple/container/releases",

        // The installer drops the binary in /usr/local/bin, which ToolLocator already searches on
        // macOS — no extra search path is needed, and naming one would only rot if Apple moves it.

        // 1.0.0 is where `container` reached its first stable release and the `--format json` output
        // this adapter parses settled; the formats were captured against 1.2.2 (see Depot
        // kontena/Notes/apple-container-cli-formats.md).
        MinimumVersion = "1.0",
    };
}
