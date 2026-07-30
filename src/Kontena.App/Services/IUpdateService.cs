using Kontena.Sdk.Models;
using Kontena.Core.Models;

namespace Kontena.App.Services;

/// <summary>An update that is available to install, as the UI needs to describe it.</summary>
/// <param name="Version">The version being offered, e.g. <c>0.2.0</c>.</param>
/// <param name="SizeBytes">Download size of the full package; 0 when the feed omits it.</param>
/// <param name="NotesMarkdown">Release notes carried in the package, or null when it has none.</param>
public sealed record AvailableUpdate(string Version, long SizeBytes, string? NotesMarkdown);

/// <summary>How far a download has got, and how fast.</summary>
/// <param name="Percent">0–100, as reported by the updater.</param>
/// <param name="BytesReceived">Derived from <see cref="Percent"/> and the package size.</param>
/// <param name="TotalBytes">Package size, or 0 when unknown.</param>
/// <param name="BytesPerSecond">Average over the transfer so far; 0 until there is enough to divide.</param>
public sealed record UpdateProgress(int Percent, long BytesReceived, long TotalBytes, double BytesPerSecond);

/// <summary>
/// Why an install cannot update itself. Not an error: a distro package or a plain unzipped build is
/// a perfectly ordinary way to run Kontena, and the UI says where to get the new version instead of
/// offering a button that cannot work.
/// </summary>
public enum UpdateSupport
{
    /// <summary>The install is managed by the updater and can replace itself.</summary>
    Supported = 0,

    /// <summary>Running from a build directory or an unpacked archive — nothing to update in place.</summary>
    NotPackaged,
}

/// <summary>
/// Checking for, downloading and applying a new version of Kontena. Behind an interface so the
/// view model can be exercised without an installed app: <see cref="VelopackUpdateService"/> only
/// does anything useful inside a packaged install, which by definition never holds in a test run.
/// </summary>
public interface IUpdateService
{
    /// <summary>Whether this install can replace itself, and why not when it cannot.</summary>
    UpdateSupport Support { get; }

    /// <summary>The version running right now.</summary>
    string CurrentVersion { get; }

    /// <summary>
    /// The stream this build was published on (KON-123). What a fresh install follows when the user has
    /// not chosen a channel — downloading a nightly is itself the choice.
    /// </summary>
    UpdateChannel BuildChannel { get; }

    /// <summary>
    /// A newer version on <paramref name="channel"/>, or null when this install is current. Also
    /// null when <see cref="Support"/> is not <see cref="UpdateSupport.Supported"/> — there is
    /// nothing to offer if it cannot be applied.
    /// </summary>
    Task<AvailableUpdate?> CheckAsync(UpdateChannel channel, CancellationToken ct = default);

    /// <summary>Fetch the update found by the last <see cref="CheckAsync"/>.</summary>
    Task DownloadAsync(IProgress<UpdateProgress> progress, CancellationToken ct = default);

    /// <summary>Apply the downloaded update and relaunch Kontena. Does not return.</summary>
    void ApplyAndRestart();

    /// <summary>Leave the downloaded update to be applied the next time Kontena starts.</summary>
    void ApplyOnNextLaunch();
}
