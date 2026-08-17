using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.Core.Models;
using Kontena.Core.Versioning;
using Kontena.Engines;
using Kontena.Engines.Fakes;
using Kontena.Sdk;
using Kontena.Sdk.Models;
using Kontena.Sdk.Orchestration.Models;

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
        /// <summary>Which products were asked about — the managed offerings each have their own.</summary>
        public List<string> Asked { get; } = [];

        public ValueTask<IReadOnlyList<ReleaseCycle>?> CyclesAsync(
            string product, CancellationToken ct = default)
        {
            Asked.Add(product);

            return ValueTask.FromResult<IReadOnlyList<ReleaseCycle>?>(
            [
                new("29", IsMaintained: true, EolFrom: null, Latest: "29.7.2"),
                new("28", IsMaintained: false, EolFrom: new DateOnly(2026, 5, 13), Latest: "28.5.2"),
                new("1.34", IsMaintained: true, EolFrom: new DateOnly(2026, 10, 27), Latest: "1.34.10"),
            ]);
        }
    }

    private sealed class ClusterEngine(string backend, string version, string distribution) : IBackend
    {
        public string Backend => backend;

        public ValueTask PingAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<BackendInfo> GetInfoAsync(CancellationToken ct = default) =>
            ValueTask.FromResult<BackendInfo>(new ClusterInfo
            {
                Backend = backend,
                DisplayName = backend,
                Kind = "Kubernetes",
                Version = version,
                Endpoint = "https://cluster.invalid",
                Distribution = distribution,
            });
    }

    private sealed class ClusterProvider(string backend, string version, string distribution) : IBackendProvider
    {
        public string Backend => backend;
        public string DisplayName => backend;
        public string Chip => "K";
        public BackendKind Kind => BackendKind.Cluster;
        public IBackend CreateBackend() => new ClusterEngine(backend, version, distribution);
    }

    /// <summary>
    /// A managed cluster is measured against its provider's own window, not upstream's (KON-95).
    /// Upstream drops 1.34 on 27 October 2026; GKE and AKS each stop on their own date, so upstream
    /// would call a still-supported cluster unsupported.
    /// </summary>
    [Theory]
    [InlineData("GKE", "google-kubernetes-engine")]
    [InlineData("AKS", "azure-kubernetes-service")]
    [InlineData("kind", "kubernetes")]
    public async Task A_cluster_is_asked_about_under_its_own_distribution(string distribution, string product)
    {
        var store = new SettingsStore(_path);
        var settings = new KontenaSettings { Onboarded = true };
        store.Save(settings);

        var calendar = new Calendar();
        var vm = new MainWindowViewModel(
            new BackendRegistry([new ClusterProvider("kubernetes:prod", "v1.34.4-gke.1043000", distribution)]),
            store,
            settings,
            new FakeUpdateService(),
            versions: new VersionSupportCheck(calendar, _cache));

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (calendar.Asked.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.Equal(product, Assert.Single(calendar.Asked));
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

    /// <summary>
    /// The dropdown rows above are happy with a bare <see cref="IBackend"/>, which the shell can probe
    /// but never open. The sidebar pill only exists once a backend is genuinely active, so these need a
    /// real <c>IContainerEngine</c> — <see cref="FakeEngine"/>, which reports 0.1.0 and cannot be told
    /// otherwise. Hence a calendar about the 0 line rather than Docker's.
    /// </summary>
    private sealed class ZeroLineCalendar(bool maintained) : IReleaseCalendar
    {
        public ValueTask<IReadOnlyList<ReleaseCycle>?> CyclesAsync(
            string product, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<ReleaseCycle>?>(
                [new("0", maintained, new DateOnly(2026, 5, 13), Latest: "0.1.0")]);
    }

    private async Task<MainWindowViewModel> OpenedShellAsync(bool maintained)
    {
        var store = new SettingsStore(_path);
        var settings = new KontenaSettings { Onboarded = true };
        store.Save(settings);

        var vm = new MainWindowViewModel(
            new BackendRegistry([new FakeEngineProvider("docker", "Docker", "D")]),
            store,
            settings,
            new FakeUpdateService(),
            versions: new VersionSupportCheck(new ZeroLineCalendar(maintained), _cache));

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!vm.IsReady && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(vm.IsReady, "the shell never opened the engine");

        // The lookup is fired before the engine is opened and is not awaited by startup, so which of
        // the two lands last is not fixed — the pill has to end up right either way.
        while (vm.EngineSupport is null && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        return vm;
    }

    /// <summary>
    /// The whole of KON-371 in one assertion: the verdict has to reach the sidebar pill, because that
    /// is the only place a user who never opens the switcher ever sees their engine's version.
    /// </summary>
    [Fact]
    public async Task The_sidebar_pill_carries_the_open_backend_verdict()
    {
        var vm = await OpenedShellAsync(maintained: false);

        Assert.True(vm.IsEngineUnsupported);
        Assert.Equal("Release 0 has not been supported since 13 May 2026.", vm.EngineSupportSummary);
    }

    [Fact]
    public async Task The_sidebar_pill_stays_quiet_on_a_maintained_release()
    {
        var vm = await OpenedShellAsync(maintained: true);

        Assert.False(vm.IsEngineUnsupported);
        Assert.Equal(string.Empty, vm.EngineSupportSummary);
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
