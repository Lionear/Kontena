using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.Core.Models;
using Kontena.Engines;
using Kontena.Engines.Fakes;
using Kontena.Sdk;
using Kontena.Sdk.Models;

namespace Kontena.App.Tests;

/// <summary>
/// One backend that never answers must not hold the whole startup (KON-357). Every backend gets its
/// own deadline — up to ten seconds across a network (KON-327, KON-329) — but the round was awaited
/// as a whole, so an engine on a network the laptop had left took the shell from 3.1 to 13.2 seconds
/// with everything else answering in under a second.
/// </summary>
public sealed class SlowProbeStartupTests
{
    /// <summary>A provider whose backend never finishes connecting — an unreachable host, without one.</summary>
    private sealed class HangingProvider : IBackendProvider
    {
        public string Backend => "hangs";
        public string DisplayName => "Hangs forever";
        public string Chip => "HG";
        public BackendKind Kind => BackendKind.Engine;
        public TimeSpan ProbeTimeout => TimeSpan.FromSeconds(30);
        public IBackend CreateBackend() => new HangingBackend();
    }

    private sealed class HangingBackend : IBackend
    {
        public string Backend => "hangs";
        public ValueTask PingAsync(CancellationToken ct = default) => new(new TaskCompletionSource().Task);
        public ValueTask<BackendInfo> GetInfoAsync(CancellationToken ct = default) =>
            new(new TaskCompletionSource<BackendInfo>().Task);
    }

    [Fact]
    public async Task A_backend_that_never_answers_does_not_hold_the_one_being_opened()
    {
        var fake = new FakeEngineProvider();
        var registry = new BackendRegistry([new HangingProvider(), fake]);
        var store = new SettingsStore(
            Path.Combine(Path.GetTempPath(), "kontena-slowprobe-" + Guid.NewGuid().ToString("N")));

        var settings = new KontenaSettings
        {
            Onboarded = true,
            Startup = StartupBackend.LastUsed,
            LastBackend = fake.Backend,
        };

        var shell = new MainWindowViewModel(
            registry, store, settings,
            updateService: new FakeUpdateService(),
            probeGrace: TimeSpan.FromMilliseconds(100));

        // The hanging probe has a thirty-second deadline of its own, so anything that waits for the
        // round waits for that. This has to land in a fraction of it.
        Assert.True(await EventuallyReadyAsync(shell), "the shell never became ready");
    }

    private static async Task<bool> EventuallyReadyAsync(MainWindowViewModel shell)
    {
        for (var i = 0; i < 50; i++)
        {
            if (shell.IsReady)
                return true;

            await Task.Delay(100);
        }

        return false;
    }
}
