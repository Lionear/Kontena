using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.Core.Models;
using Kontena.Engines;
using Kontena.Engines.Fakes;

namespace Kontena.App.Tests;

/// <summary>
/// A second main window, opened on the connection picked in the switcher (KON-424) — so Docker and a
/// cluster can be on screen at once instead of taking turns in one window.
/// <para>
/// The shell is what these are about. Making the window is five lines in
/// <c>MainWindow.OnOpenBackendWindowClick</c>; deciding which backend it lands on, and what it is
/// allowed to write down while doing so, is here.
/// </para>
/// </summary>
public sealed class NewBackendWindowTests
{
    private static readonly FakeEngineProvider EngineA = new("fake-a", "Engine A", "A");
    private static readonly FakeEngineProvider EngineB = new("fake-b", "Engine B", "B");

    private static (BackendRegistry Registry, SettingsStore Store, KontenaSettings Settings) Shell(string? lastBackend)
    {
        var store = new SettingsStore(
            Path.Combine(Path.GetTempPath(), "kontena-newwindow-" + Guid.NewGuid().ToString("N")));

        var settings = new KontenaSettings
        {
            Onboarded = true,
            Startup = StartupBackend.LastUsed,
            LastBackend = lastBackend,
        };

        store.Save(settings);

        return (new BackendRegistry([EngineA, EngineB]), store, settings);
    }

    [Fact]
    public async Task A_window_opened_on_a_backend_lands_there_and_not_on_the_remembered_one()
    {
        var (registry, store, settings) = Shell(lastBackend: EngineA.Backend);

        var shell = new MainWindowViewModel(
            registry, store, settings,
            updateService: new FakeUpdateService(),
            probeGrace: TimeSpan.FromMilliseconds(100),
            openBackend: EngineB.Backend);

        Assert.True(await EventuallyReadyAsync(shell), "the shell never became ready");
        Assert.Equal(EngineB.DisplayName, shell.EngineName);
    }

    [Fact]
    public async Task The_window_it_was_spawned_from_stays_where_it_was()
    {
        var (registry, store, settings) = Shell(lastBackend: EngineA.Backend);

        var first = new MainWindowViewModel(
            registry, store, settings,
            updateService: new FakeUpdateService(),
            probeGrace: TimeSpan.FromMilliseconds(100));

        Assert.True(await EventuallyReadyAsync(first), "the first shell never became ready");
        Assert.Equal(EngineA.DisplayName, first.EngineName);

        var second = first.OpenInNewWindow(EngineB.Backend);

        Assert.True(await EventuallyReadyAsync(second), "the second shell never became ready");
        Assert.Equal(EngineB.DisplayName, second.EngineName);

        // Two connections, two windows — the whole point. The first one switching along with the
        // second would be the behaviour this ticket exists to get rid of.
        Assert.Equal(EngineA.DisplayName, first.EngineName);
    }

    /// <summary>
    /// The startup backend belongs to the window the user launched. A spawned window whose backend
    /// has gone away in the meantime says so, and leaves that setting alone — the launch path clears
    /// it, and doing that here would silently move where the next launch lands.
    /// </summary>
    [Fact]
    public async Task A_backend_that_vanished_does_not_rewrite_what_the_next_launch_opens()
    {
        var (registry, store, settings) = Shell(lastBackend: EngineA.Backend);

        var shell = new MainWindowViewModel(
            registry, store, settings,
            updateService: new FakeUpdateService(),
            probeGrace: TimeSpan.FromMilliseconds(100),
            openBackend: "fake-gone");

        Assert.True(await EventuallyAsync(() => shell.IsBackendDown), "the shell never reported the backend down");
        Assert.Equal(EngineA.Backend, store.Load().LastBackend);
    }

    private static Task<bool> EventuallyReadyAsync(MainWindowViewModel shell) => EventuallyAsync(() => shell.IsReady);

    private static async Task<bool> EventuallyAsync(Func<bool> condition)
    {
        for (var i = 0; i < 50; i++)
        {
            if (condition())
                return true;

            await Task.Delay(100);
        }

        return false;
    }
}
