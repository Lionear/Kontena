using System.Diagnostics;
using Kontena.Core.Models;
using Velopack;
using Velopack.Sources;

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

    public string CurrentVersion { get; } =
        typeof(VelopackUpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// Read from the assembly's informational version, which carries the full string the workflow
    /// stamped — <c>0.2.0-nightly.20260726.26</c>. The assembly *version* cannot answer this: it is
    /// numeric only, so the prerelease tag that names the channel is gone by the time it is written.
    /// </summary>
    public UpdateChannel BuildChannel { get; } = ReleaseChannel.FromVersion(
        typeof(VelopackUpdateService).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion);

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

    private UpdateManager ManagerFor(UpdateChannel channel)
    {
        _channel = channel;

        // Both rolling streams are published as GitHub prereleases, so the source has to be told to
        // look at them; the channel below is what actually decides which feed is read.
        var source = new GithubSource(RepositoryUrl, null, prerelease: channel != UpdateChannel.Stable);
        return new UpdateManager(source, new UpdateOptions
        {
            ExplicitChannel = ReleaseChannel.ForCurrentPlatform(channel),
        });
    }
}
