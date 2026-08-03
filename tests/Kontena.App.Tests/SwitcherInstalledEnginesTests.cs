using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.Core.Models;
using Kontena.Engines;
using Kontena.Engines.Fakes;
using Kontena.Sdk;
using Kontena.Sdk.Models;

namespace Kontena.App.Tests;

/// <summary>
/// The switcher leaves out a built-in engine this machine shows no sign of having (KON-255).
/// <para>
/// The catalog offers Docker and Podman whether or not they are installed, so on a Docker-only machine
/// Podman sat in the switcher forever as a row that says "Not connected" and cannot be clicked. The
/// distinction that matters is not-installed versus installed-but-stopped: a stopped engine is exactly
/// what someone opens the switcher to find out about, so it stays.
/// </para>
/// </summary>
public sealed class SwitcherInstalledEnginesTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"kontena-switcher-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    /// <summary>A built-in-shaped engine: it says whether it is installed, and whether it answers.</summary>
    private sealed class TestEngineProvider(string backend, bool installed, bool answers) : IBackendProvider
    {
        public string Backend => backend;
        public string DisplayName => backend;
        public string Chip => backend[..1].ToUpperInvariant();
        public BackendKind Kind => BackendKind.Engine;
        public bool IsInstalled => installed;

        public IBackend CreateBackend() => answers
            ? new FakeEngine()
            : throw new InvalidOperationException($"{backend} is not running");
    }

    /// <summary>Stands in for a kube-context: contributed because a user added it, never absent.</summary>
    private sealed class TestClusterProvider(string backend) : IBackendProvider
    {
        public string Backend => backend;
        public string DisplayName => backend;
        public string Chip => "K";
        public BackendKind Kind => BackendKind.Cluster;
        public IBackend CreateBackend() => throw new InvalidOperationException("never reachable here");
    }

    private async Task<MainWindowViewModel> ShellAsync(
        IEnumerable<IBackendProvider> providers, KontenaSettings? settings = null)
    {
        var store = new SettingsStore(_path);
        var resolved = settings ?? new KontenaSettings { Onboarded = true };
        store.Save(resolved);

        var vm = new MainWindowViewModel(
            new BackendRegistry([.. providers]), store, resolved, new FakeUpdateService());

        // The list is built during startup, whether or not anything ends up connected.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!vm.IsReady && !vm.IsBackendDown && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(vm.IsReady || vm.IsBackendDown, "the shell never finished starting");
        return vm;
    }

    [Fact]
    public async Task An_engine_with_no_trace_on_this_machine_is_left_out()
    {
        var vm = await ShellAsync(
        [
            new TestEngineProvider("docker", installed: true, answers: true),
            new TestEngineProvider("podman", installed: false, answers: false),
        ]);

        Assert.Equal("docker", Assert.Single(vm.Engines).Backend);
    }

    [Fact]
    public async Task An_engine_that_is_installed_but_stopped_stays_in_the_list()
    {
        // The whole point of the distinction: "it is here, it is not running" is information, and this
        // row is the one that carries it — plus the retry KON-328 put on it.
        var vm = await ShellAsync(
        [
            new TestEngineProvider("docker", installed: true, answers: false),
        ]);

        var row = Assert.Single(vm.Engines);
        Assert.Equal("docker", row.Backend);
        Assert.False(row.IsConnected);
    }

    [Fact]
    public async Task The_active_backend_is_never_hidden_even_if_it_claims_to_be_absent()
    {
        // It answered and the shell is connected to it; a provider saying otherwise does not get to
        // remove the row the user is standing on.
        var vm = await ShellAsync(
        [
            new TestEngineProvider("docker", installed: false, answers: true),
        ]);

        var row = Assert.Single(vm.Engines);
        Assert.True(row.IsActive);
    }

    [Fact]
    public async Task The_backend_startup_would_open_stays_so_the_gone_message_has_a_row()
    {
        // ConnectPreferredAsync says "… is gone" about the pinned backend. Hiding it would leave that
        // sentence pointing at nothing.
        var settings = new KontenaSettings
        {
            Onboarded = true,
            PinnedBackend = "podman",
            Startup = StartupBackend.Pinned,
        };

        var vm = await ShellAsync(
        [
            new TestEngineProvider("docker", installed: true, answers: true),
            new TestEngineProvider("podman", installed: false, answers: false),
        ], settings);

        Assert.Contains(vm.Engines, e => e.Backend == "podman");
    }

    [Fact]
    public async Task With_nothing_installed_the_down_card_says_that_rather_than_blaming_a_socket()
    {
        // The switcher is now empty on such a machine, so this card is the only thing left that can
        // say why. "The engine may be stopped or still starting" would send someone looking for a
        // daemon that was never installed.
        var vm = await ShellAsync(
        [
            new TestEngineProvider("docker", installed: false, answers: false),
            new TestEngineProvider("podman", installed: false, answers: false),
        ]);

        Assert.True(vm.IsBackendDown);
        Assert.Empty(vm.Engines);
        Assert.Equal("No container engine found", vm.BackendDownTitle);
        Assert.Contains("no sign of Docker or Podman", vm.BackendDownDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_an_engine_installed_but_silent_the_down_card_still_blames_the_socket()
    {
        var vm = await ShellAsync(
        [
            new TestEngineProvider("docker", installed: true, answers: false),
        ]);

        Assert.True(vm.IsBackendDown);
        Assert.Equal("Can't reach a container engine", vm.BackendDownTitle);
    }

    [Fact]
    public async Task A_cluster_is_not_subject_to_this_at_all()
    {
        // It is in the list because someone added it. IsInstalled defaults to true, so it never even
        // reaches the question — pinned here so that default is what keeps it, not the fallbacks.
        var vm = await ShellAsync(
        [
            new TestEngineProvider("docker", installed: true, answers: true),
            new TestClusterProvider("kubernetes:kind-dev"),
        ]);

        Assert.Contains(vm.Clusters, c => c.Backend == "kubernetes:kind-dev");
    }
}
