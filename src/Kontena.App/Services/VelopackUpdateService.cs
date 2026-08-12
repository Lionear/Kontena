using System.Diagnostics;
using Kontena.Sdk.Models;
using Velopack;
using Velopack.Sources;
using Kontena.Core.Models;

namespace Kontena.App.Services;

/// <summary>
/// <see cref="IUpdateService"/> on top of Velopack, reading the releases published by the Build
/// workflow to this repository's GitHub releases.
/// <para>
/// One <see cref="UpdateManager"/> is built per check rather than once, because the channel is a
/// construction-time option and the user can change it in Settings while the app runs.
/// </para>
/// </summary>
public sealed class VelopackUpdateService : IUpdateService
{
    private const string RepositoryUrl = "https://github.com/Lionear/Kontena";

    private UpdateInfo? _pending;

    private UpdateSupport? _support;

    /// <summary>
    /// Velopack only manages an install it created. A build directory, an unzipped archive or a
    /// distro package all report <see cref="UpdateSupport.NotPackaged"/>, and the UI then points at
    /// the download page rather than offering a restart that would do nothing.
    /// <para>
    /// Answered once and remembered: bindings read this repeatedly, and it cannot change while the
    /// process runs. Constructing the manager throws in a host that never called
    /// <c>VelopackApp.Build().Run()</c> — the screenshot renderer, a test, anything embedding these
    /// view models — and a property that throws inside a binding fails *silently*, leaving the
    /// control at its default visibility. So the throw is caught and read as "not packaged", which
    /// is exactly what such a host is.
    /// </para>
    /// </summary>
    public UpdateSupport Support => _support ??= Probe();

    private static UpdateSupport Probe()
    {
        try
        {
            return new UpdateManager(RepositoryUrl).IsInstalled
                ? UpdateSupport.Supported
                : UpdateSupport.NotPackaged;
        }
        catch (Exception)
        {
            return UpdateSupport.NotPackaged;
        }
    }

    public string CurrentVersion { get; } = ReadCurrentVersion();

    /// <summary>
    /// Preferably the version in the installed package's manifest, because that is the number
    /// <see cref="UpdateManager.CheckForUpdatesAsync"/> compares the feed against. Anything else can
    /// disagree with the updater, and then "it keeps offering the version I just installed" reads as
    /// a wrong label on the card instead of the mismatch it actually is.
    /// <para>
    /// An install the updater does not manage has no manifest to read, so there the build's own
    /// <see cref="AppVersion.Current"/> is both the honest answer and the only one.
    /// </para>
    /// </summary>
    private static string ReadCurrentVersion()
    {
        try
        {
            var manager = new UpdateManager(RepositoryUrl);
            if (manager.IsInstalled && manager.CurrentVersion is { } installed)
                return installed.ToFullString();
        }
        catch (Exception)
        {
            // Same reason Probe() swallows: constructing the manager throws in a host that never ran
            // VelopackApp, and a throwing property inside a binding fails silently.
        }

        return AppVersion.Current;
    }

    /// <summary>
    /// Read from the version this build carries, which is the full string the workflow stamped —
    /// <c>0.2.0-nightly.20260726.26</c>. The assembly *version* cannot answer this: it is numeric
    /// only, so the prerelease tag that names the channel is gone by the time it is written.
    /// </summary>
    public UpdateChannel BuildChannel { get; } = ReleaseChannel.FromVersion(AppVersion.Current);

    public async Task<AvailableUpdate?> CheckAsync(UpdateChannel channel, CancellationToken ct = default)
    {
        if (Support != UpdateSupport.Supported)
            return null;

        var manager = ManagerFor(channel);
        var info = await manager.CheckForUpdatesAsync().WaitAsync(ct).ConfigureAwait(false);
        _pending = info;

        if (info is null)
            return null;

        var target = info.TargetFullRelease;
        return new AvailableUpdate(target.Version.ToString(), target.Size, target.NotesMarkdown);
    }

    public async Task DownloadAsync(IProgress<UpdateProgress> progress, CancellationToken ct = default)
    {
        var info = _pending
            ?? throw new InvalidOperationException("No update has been found to download.");

        // Velopack reports a percentage only. The card shows megabytes and a rate, which are the
        // numbers that tell you whether a stalled-looking bar is actually moving, so derive them
        // from the package size and the time spent so far.
        var total = info.TargetFullRelease.Size;
        var started = Stopwatch.StartNew();

        void Report(int percent)
        {
            var received = total > 0 ? (long)(total * (percent / 100.0)) : 0;
            var seconds = started.Elapsed.TotalSeconds;
            progress.Report(new UpdateProgress(
                percent, received, total, seconds > 0.5 ? received / seconds : 0));
        }

        await ManagerFor(_channel).DownloadUpdatesAsync(info, Report, ct).ConfigureAwait(false);
    }

    public void ApplyAndRestart()
    {
        var info = _pending ?? throw new InvalidOperationException("No update has been downloaded.");
        ManagerFor(_channel).ApplyUpdatesAndRestart(info.TargetFullRelease);
    }

    public void ApplyOnNextLaunch()
    {
        var info = _pending ?? throw new InvalidOperationException("No update has been downloaded.");

        // Staged, not applied: the swap happens when this process exits, and the app does not come
        // back up by itself — the user said "next launch", not "now".
        ManagerFor(_channel).WaitExitThenApplyUpdates(info.TargetFullRelease, silent: true, restart: false);
    }

    private UpdateChannel _channel = UpdateChannel.Stable;

    /// <summary>
    /// Where a channel's release assets live. Stable is whatever tag GitHub currently calls "latest
    /// release"; the two rolling streams are always republished onto the same fixed tag by the Build
    /// workflow — an atomic staging-tag swap (build.yml) keeps that tag from ever briefly missing a
    /// release — so their assets sit at a fixed URL too. Either way this is a plain <c>github.com</c>
    /// download URL, never <c>api.github.com</c> (KON-312): unlike <see cref="GithubSource"/>, nothing
    /// here is rate-limited to 60 requests an hour.
    /// </summary>
    internal static string BaseUrlFor(string repositoryUrl, UpdateChannel channel) =>
        channel == UpdateChannel.Stable
            ? $"{repositoryUrl}/releases/latest/download"
            : $"{repositoryUrl}/releases/download/{ReleaseChannel.Stream(channel)}";

    /// <summary>
    /// Without <see cref="UpdateOptions.AllowVersionDowngrade"/> the updater answers "no updates" to
    /// every channel whose newest build is semver-lower than the running one, and the channel is part
    /// of the version: the prerelease tag is compared as text, so <c>0.4.0-nightly.…</c> sorts below
    /// <c>0.4.0-preview.…</c>. Switching preview → nightly was therefore silently impossible (KON-372).
    /// <para>
    /// Allowed only when the target channel is not the one this build came from. A switch is a jump to
    /// a different stream that the user asked for by name; the default guards against a feed on *your
    /// own* channel rolling backwards, and that stays guarded.
    /// </para>
    /// </summary>
    internal static UpdateOptions OptionsFor(UpdateChannel channel, UpdateChannel buildChannel) => new()
    {
        ExplicitChannel = ReleaseChannel.ForCurrentPlatform(channel),
        AllowVersionDowngrade = channel != buildChannel,
    };

    private UpdateManager ManagerFor(UpdateChannel channel)
    {
        _channel = channel;

        var source = new SimpleWebSource(BaseUrlFor(RepositoryUrl, channel));
        return new UpdateManager(source, OptionsFor(channel, BuildChannel));
    }
}
