namespace Kontena.Core.Tooling;

/// <summary>
/// Finds the newest release of a tool and the checksum that goes with it. Separate from the installer
/// so the download path can be tested without reaching the network — and so a machine that is offline
/// gets a clear "could not look it up" rather than a failed install.
/// </summary>
public interface IToolReleaseSource
{
    /// <summary>
    /// The latest release for this machine, or null when there is none to offer — no release spec, an
    /// architecture nobody builds for, or a publisher that stopped shipping a checksum.
    /// </summary>
    ValueTask<ToolDownload?> LatestAsync(ExternalTool tool, CancellationToken ct = default);
}
