using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.Sdk.Models;
using Kontena.Sdk;
using Kontena.Core.Models;
using Kontena.Engines;

namespace Kontena.App.Tests;

/// <summary>
/// Settings, Activity and About stay reachable while no backend is (KON-137).
/// <para>
/// These are the three pages you want most when nothing works — Settings is where the connection
/// gets fixed, Activity is what happened just before it broke, About is what you need to report it.
/// Before this they were all reachable in the sidebar and none of them rendered: the shell showed
/// the engine-down card in their place, so clicking one looked like the app had swallowed it.
/// </para>
/// </summary>
public sealed class BackendDownPagesTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"kontena-down-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    /// <summary>
    /// A shell with nothing to connect to. An empty registry has no provider that can answer, which
    /// is the shortest route to the state under test — and a deterministic one, where waiting on a
    /// real engine to fail would not be.
    /// </summary>
    private async Task<MainWindowViewModel> DownShellAsync()
    {
        var store = new SettingsStore(_path);
        var settings = new KontenaSettings { Onboarded = true };
        store.Save(settings);

        var vm = new MainWindowViewModel(
            new BackendRegistry([]), store, settings, new FakeUpdateService());

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!vm.IsBackendDown && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(vm.IsBackendDown, "the shell never reached the engine-down state");
        return vm;
    }

    [Fact]
    public async Task With_no_backend_the_down_card_has_the_content_area()
    {
        var vm = await DownShellAsync();

        Assert.True(vm.IsBackendDownVisible);
        Assert.False(vm.IsPageVisible);
        Assert.False(vm.IsConnecting);
    }

    /// <summary>
    /// Settings is still reachable with nothing connected (KON-137) — but since KON-437 it does that by
    /// opening <i>over</i> the card rather than by taking the content area from it, so the card stays
    /// exactly where it was and is there again when Settings closes.
    /// </summary>
    [Fact]
    public async Task Settings_opens_over_the_down_card()
    {
        var vm = await DownShellAsync();

        vm.ShowSettingsCommand.Execute(null);

        Assert.True(vm.IsSettingsOpen);
        Assert.True(vm.IsBackendDownVisible);

        vm.CloseSettingsCommand.Execute(null);

        Assert.False(vm.IsSettingsOpen);
        Assert.True(vm.IsBackendDownVisible);
    }

    [Fact]
    public async Task About_shows_over_the_down_card()
    {
        var vm = await DownShellAsync();

        vm.ShowAboutCommand.Execute(null);

        Assert.True(vm.IsAboutSelected);
        Assert.True(vm.IsPageVisible);
        Assert.False(vm.IsBackendDownVisible);
    }

    /// <summary>
    /// Activity used to be built on connect, so with no connection there was nothing to navigate to
    /// and the command returned without doing anything — the dead button of KON-117, in the one
    /// state where the page is most worth reading.
    /// </summary>
    [Fact]
    public async Task Activity_exists_and_shows_without_ever_having_connected()
    {
        var vm = await DownShellAsync();

        Assert.NotNull(vm.Activity);

        vm.ShowActivityCommand.Execute(null);

        Assert.True(vm.IsActivitySelected);
        Assert.True(vm.IsPageVisible);
        Assert.False(vm.IsBackendDownVisible);
    }

    /// <summary>
    /// Leaving one of the three puts the down card back. Otherwise the state that explains why
    /// nothing else works would be gone for the rest of the session.
    /// </summary>
    [Fact]
    public async Task Leaving_the_page_gives_the_down_card_back()
    {
        var vm = await DownShellAsync();

        vm.ShowSettingsCommand.Execute(null);
        vm.CurrentPage = null;

        Assert.True(vm.IsBackendDownVisible);
        Assert.False(vm.IsPageVisible);
    }
}
