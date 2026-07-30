using Kontena.Sdk.Models;
using Kontena.Core.Models;

namespace Kontena.App.Services;

/// <summary>
/// An updater that always has something on offer, without a server or a packaged install — the
/// counterpart of the fake engine and fake cluster providers.
/// <para>
/// It exists because the interesting states of the update card only occur on a machine that is both
/// packaged and behind: a developer run reports <see cref="UpdateSupport.NotPackaged"/> and shows
/// the one state that says so. This renders the others, and is what the screenshot harness drives.
/// </para>
/// </summary>
public sealed class FakeUpdateService : IUpdateService
{
    private readonly bool _fail;
    private readonly int? _holdAt;

    /// <param name="fail">Fail the download, to render the failure state.</param>
    /// <param name="holdAt">
    /// Percentage to stop at and stay at, instead of running to completion. Without it a capture of
    /// "downloading" is a race the renderer loses: pumping the dispatcher to reach 62% also carries
    /// it to 100%, and the shot shows the finished state instead.
    /// </param>
    /// <param name="buildChannel">
    /// The stream to claim this build came from (KON-123). Stable unless a scene is about what a
    /// nightly download does on first launch.
    /// </param>
    public FakeUpdateService(bool fail = false, int? holdAt = null,
        UpdateChannel buildChannel = UpdateChannel.Stable)
    {
        BuildChannel = buildChannel;
        _fail = fail;
        _holdAt = holdAt;
    }

    public UpdateSupport Support => UpdateSupport.Supported;

    public string CurrentVersion => "0.1.0";

    public UpdateChannel BuildChannel { get; }

    public Task<AvailableUpdate?> CheckAsync(UpdateChannel channel, CancellationToken ct = default) =>
        Task.FromResult<AvailableUpdate?>(new AvailableUpdate("0.2.0", 68 * 1000 * 1000,
            """
            **Kubernetes clusters.** Switch between engines and kube-contexts from the same sidebar.

            **Extension store.** Install third-party backends with explicit, per-extension permissions.

            **Changed** — Prune now skips externally managed containers instead of asking twice.

            **Fixed** — Terminal font settings applied to already-open terminals.
            """));

    public async Task DownloadAsync(IProgress<UpdateProgress> progress, CancellationToken ct = default)
    {
        const long total = 68 * 1000 * 1000;
        for (var percent = 0; percent <= 100; percent += 2)
        {
            ct.ThrowIfCancellationRequested();
            if (_fail && percent == 62)
                throw new HttpRequestException("connection reset");

            progress.Report(new UpdateProgress(percent, total * percent / 100, total, 4.1 * 1000 * 1000));

            if (percent == _holdAt)
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);

            await Task.Delay(30, ct).ConfigureAwait(false);
        }
    }

    public void ApplyAndRestart()
    {
    }

    public void ApplyOnNextLaunch()
    {
    }
}
