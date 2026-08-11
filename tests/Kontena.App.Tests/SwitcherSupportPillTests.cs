using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.Core.Models;
using Kontena.Core.Versioning;
using Kontena.Engines;
using Kontena.Sdk;
using Kontena.Sdk.Models;

namespace Kontena.App.Tests;

/// <summary>
/// A switcher row says when its backend runs a release the publisher has dropped (KON-370). The answer
/// arrives after the row is drawn — it is the one thing here that touches the network — so the row is
/// rebuilt when it lands.
/// </summary>
public sealed class SwitcherSupportPillTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"kontena-support-{Guid.NewGuid():N}.json");

    private readonly string _cache = Path.Combine(
        Path.GetTempPath(), $"kontena-support-cache-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);

        if (Directory.Exists(_cache))
            Directory.Delete(_cache, recursive: true);
    }

    private sealed class VersionedEngine(string backend, string version) : IBackend
    {
        public string Backend => backend;

        public ValueTask PingAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<BackendInfo> GetInfoAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(new BackendInfo
            {
                Backend = backend,
                DisplayName = backend,
                Version = version,
                Endpoint = "unix:///var/run/test.sock",
            });
    }

    private sealed class VersionedProvider(string backend, string version) : IBackendProvider
    {
        public string Backend => backend;
        public string DisplayName => backend;
        public string Chip => backend[..1].ToUpperInvariant();
        public BackendKind Kind => BackendKind.Engine;
        public IBackend CreateBackend() => new VersionedEngine(backend, version);
    }

    private sealed class Calendar : IReleaseCalendar
    {
        public ValueTask<IReadOnlyList<ReleaseCycle>?> CyclesAsync(
            string product, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<ReleaseCycle>?>(
            [
                new("29", IsMaintained: true, EolFrom: null, Latest: "29.7.2"),
                new("28", IsMaintained: false, EolFrom: new DateOnly(2026, 5, 13), Latest: "28.5.2"),
            ]);
    }

    private async Task<MainWindowViewModel> ShellAsync(string version)
    {
        var store = new SettingsStore(_path);
        var settings = new KontenaSettings { Onboarded = true };
        store.Save(settings);

        var vm = new MainWindowViewModel(
            new BackendRegistry([new VersionedProvider("docker", version)]),
            store,
            settings,
            new FakeUpdateService(),
            versions: new VersionSupportCheck(new Calendar(), _cache));

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!vm.IsReady && !vm.IsBackendDown && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(vm.IsReady || vm.IsBackendDown, "the shell never finished starting");
        return vm;
    }

    /// <summary>The support answer lands after the switcher is drawn, so give it a moment to arrive.</summary>
    private static async Task<EngineOption> SettledRowAsync(MainWindowViewModel vm)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (vm.Engines.Count > 0 && vm.Engines[0].Support is null && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        return Assert.Single(vm.Engines);
    }

    [Fact]
    public async Task A_backend_on_a_dropped_release_gets_the_pill()
    {
        var row = await SettledRowAsync(await ShellAsync("28.5.2"));

        Assert.True(row.IsUnsupported);
        Assert.Equal("Release 28 has not been supported since 13 May 2026.", row.SupportSummary);
    }

    [Fact]
    public async Task A_backend_on_a_maintained_release_gets_none()
    {
        var row = await SettledRowAsync(await ShellAsync("29.7.2"));

        Assert.False(row.IsUnsupported);
    }

    [Fact]
    public async Task A_backend_with_no_published_calendar_is_never_asked_about()
    {
        var store = new SettingsStore(_path);
        var settings = new KontenaSettings { Onboarded = true };
        store.Save(settings);

        var vm = new MainWindowViewModel(
            new BackendRegistry([new VersionedProvider("apple", "0.5.0")]),
            store,
            settings,
            new FakeUpdateService(),
            versions: new VersionSupportCheck(new Calendar(), _cache));

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!vm.IsReady && !vm.IsBackendDown && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        // Apple's `container` is published nowhere we can read. Silence, not a guess — and not the
        // calendar's Docker answer applied to something that is not Docker.
        Assert.Null(Assert.Single(vm.Engines).Support);
    }
}
