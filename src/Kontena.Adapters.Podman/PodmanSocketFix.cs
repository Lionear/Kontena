using Kontena.Sdk.Tooling;

namespace Kontena.Adapters.Podman;

/// <summary>
/// The most common reason Kontena reports Podman as unreachable while <c>podman ps</c> works fine
/// from a terminal: the CLI talks to local storage directly, but Kontena needs the Docker-compatible
/// API socket that <c>podman.socket</c> opens — and on a fresh rootless install, that unit is present
/// but was never enabled.
/// </summary>
public static class PodmanSocketFix
{
    private static readonly ExternalTool Systemctl = new("systemctl", "systemctl", ["--version"], []);

    /// <summary>
    /// Enables and starts Podman's own user-scoped socket unit. A <c>--user</c> unit needs no
    /// elevation — running it with sudo would manage the wrong (system-wide) unit instead, which is
    /// not the one rootless Podman listens through.
    /// </summary>
    public static readonly ToolInvocation EnableSocket =
        new(Systemctl, ["--user", "enable", "--now", "podman.socket"]);

    /// <summary>
    /// True when the fix above would actually help: Linux, systemd present, and the unit exists but
    /// is not running. "Active" means something else is wrong; "not found" means Podman was not
    /// installed through a package that ships the unit — offering to enable it would send the user
    /// after a fix that cannot work.
    /// </summary>
    public static async ValueTask<bool> IsFixableAsync(IToolRunner runner, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux())
            return false;

        try
        {
            var result = await runner.RunAsync(
                new ToolInvocation(Systemctl, ["--user", "is-active", "podman.socket"]), ct);

            return result.StandardOutput.Trim() == "inactive";
        }
        catch (ToolNotFoundException)
        {
            return false;
        }
    }
}
