using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.Sdk;
using Kontena.Sdk.Errors;
using Kontena.Core.Models;
using Kontena.Engines;
using Kontena.Engines.Fakes;

namespace Kontena.App.Tests;

/// <summary>
/// A backend that did not answer can be asked again (KON-327 / KON-328).
/// <para>
/// The probe result was a one-off cache with no reachable refresh: an engine that was still starting
/// when Kontena launched — Docker Desktop routinely is — stayed dead in the switcher for the rest of
/// the session, its row a button that did nothing. The only thing that re-probed was the down card's
/// Reconnect, which is not on screen when something else did connect.
/// </para>
/// </summary>
public sealed class RetryBackendTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"kontena-retry-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    /// <summary>
    /// A shell holding one engine that is down, with something else connected — the case the down
    /// card does not cover, and the whole reason a per-row retry has to exist.
    /// </summary>
    private async Task<(MainWindowViewModel Shell, FlakyProvider Flaky)> ShellAsync()
    {
        var store = new SettingsStore(_path);
        var settings = new KontenaSettings { Onboarded = true };
        store.Save(settings);

        var flaky = new FlakyProvider();

        // Not the "fake" identity: auto-connect skips the demo backend on purpose, and the state under
        // test is the one where something else *did* connect, so the down card is not on screen.
        var steady = new FakeEngineProvider(backend: "steady", displayName: "Steady");

        var vm = new MainWindowViewModel(
            new BackendRegistry([steady, flaky]), store, settings,
            new FakeUpdateService(), buildCatalog: (_, _, _, _) => []);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!vm.IsReady && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(vm.IsReady, "the shell never connected to the fake engine");
        return (vm, flaky);
    }

    private static EngineOption Row(MainWindowViewModel vm, string backend) =>
        vm.Engines.Single(e => e.Backend == backend);

    [Fact]
    public async Task An_unreachable_row_is_not_a_dead_button()
    {
        var (vm, _) = await ShellAsync();

        var row = Row(vm, "flaky");

        Assert.False(row.IsConnected);
        Assert.NotNull(row.SwitchCommand);
        Assert.True(row.CanRetry);
    }

    [Fact]
    public async Task Retrying_a_backend_that_is_up_now_opens_it()
    {
        var (vm, flaky) = await ShellAsync();
        flaky.Live = true;

        await vm.RetryBackendCommand.ExecuteAsync("flaky");

        Assert.True(Row(vm, "flaky").IsConnected);
        Assert.True(Row(vm, "flaky").IsActive);
        Assert.True(vm.IsReady);
    }

    /// <summary>
    /// Still down is still offered. A retry that answered "no" once used to be how a backend became
    /// permanently unreachable; the row has to come back out of the attempt in the state it went in.
    /// </summary>
    [Fact]
    public async Task Retrying_a_backend_that_is_still_down_leaves_it_retryable()
    {
        var (vm, _) = await ShellAsync();

        await vm.RetryBackendCommand.ExecuteAsync("flaky");

        var row = Row(vm, "flaky");
        Assert.False(row.IsConnected);
        Assert.False(row.IsRetrying);
        Assert.True(row.CanRetry);
        Assert.NotNull(row.SwitchCommand);
    }

    /// <summary>
    /// Settings retries in place: the answer reaches the row and the switcher, and the page is the
    /// same instance afterwards — rebuilding it would empty a remote form halfway typed next to the
    /// row that was just retried.
    /// </summary>
    [Fact]
    public async Task Retrying_from_settings_updates_the_row_and_the_switcher()
    {
        var (vm, flaky) = await ShellAsync();
        var page = vm.SettingsPage!;
        flaky.Live = true;

        await page.RetryBackendCommand.ExecuteAsync("flaky");

        Assert.True(page.Engines.Single(e => e.Backend == "flaky").Connected);
        Assert.True(Row(vm, "flaky").IsConnected);
        Assert.Same(page, vm.SettingsPage);

        // And it does not drag the user out of Settings on its way — that is the switcher's job.
        Assert.False(Row(vm, "flaky").IsActive);
    }

    /// <summary>An engine that answers only once someone has started it — Docker Desktop, an SSH
    /// agent waiting on approval, a VPN that is still coming up.</summary>
    private sealed class FlakyProvider : IBackendProvider
    {
        private readonly FakeEngineProvider _running = new(backend: "flaky", displayName: "Flaky");

        public bool Live { get; set; }

        public string Backend => "flaky";
        public string DisplayName => "Flaky";
        public string Chip => "F";
        public BackendKind Kind => BackendKind.Engine;

        public IBackend CreateBackend() => Live
            ? _running.CreateBackend()
            : throw new EngineUnreachableException("not started yet");
    }
}
