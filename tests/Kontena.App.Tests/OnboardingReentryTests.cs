using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.Core.Models;
using Kontena.Engines;

namespace Kontena.App.Tests;

/// <summary>
/// Skipping the first-run wizard is reversible.
/// <para>
/// <c>Onboarded</c> is a latch, so before this the wizard ran once ever: skip it and the app went on
/// picking the first engine that answered, without ever asking again. Reconnect restores the
/// connection but not the choice — "Set up again" restores the choice.
/// </para>
/// </summary>
public sealed class OnboardingReentryTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"kontena-onboard-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    /// <summary>
    /// A first launch with nothing to connect to. An empty registry has no provider that can answer,
    /// which is the shortest deterministic route to both states under test.
    /// </summary>
    private async Task<(MainWindowViewModel Vm, SettingsStore Store)> FirstRunAsync()
    {
        var store = new SettingsStore(_path);
        var settings = new KontenaSettings { Onboarded = false };
        store.Save(settings);

        var vm = new MainWindowViewModel(
            new BackendRegistry([]), store, settings, new FakeUpdateService());

        await WaitFor(() => vm.IsOnboarding, "the shell never showed the onboarding wizard");
        return (vm, store);
    }

    private static async Task WaitFor(Func<bool> condition, string complaint)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(condition(), complaint);
    }

    [Fact]
    public async Task Skipping_leaves_the_wizard_and_marks_it_done()
    {
        var (vm, _) = await FirstRunAsync();

        vm.Onboarding!.SkipCommand.Execute(null);

        await WaitFor(() => vm.IsBackendDown, "skipping never landed anywhere");
        Assert.False(vm.IsOnboarding);
        Assert.Null(vm.Onboarding);
        Assert.True(new SettingsStore(_path).Load().Onboarded);
    }

    [Fact]
    public async Task Set_up_again_brings_the_wizard_back_after_a_skip()
    {
        var (vm, _) = await FirstRunAsync();

        vm.Onboarding!.SkipCommand.Execute(null);
        await WaitFor(() => vm.IsBackendDown, "skipping never landed anywhere");

        await vm.RunSetupCommand.ExecuteAsync(null);

        Assert.True(vm.IsOnboarding);
        Assert.NotNull(vm.Onboarding);
        Assert.False(vm.IsBackendDown);
        Assert.False(vm.IsBackendDownVisible);
    }

    /// <summary>
    /// A rescan builds a fresh view model, so anything the user already set on the screen has to be
    /// carried across — the toggle springing back to the stored value would read as the app undoing
    /// the choice every time you pressed Rescan.
    /// </summary>
    [Fact]
    public async Task Rescanning_keeps_the_auto_detect_toggle_where_the_user_left_it()
    {
        var (vm, _) = await FirstRunAsync();

        var before = vm.Onboarding!.AutoDetect;
        vm.Onboarding.AutoDetect = !before;

        await vm.Onboarding.RescanCommand.ExecuteAsync(null);

        Assert.True(vm.IsOnboarding);
        Assert.Equal(!before, vm.Onboarding!.AutoDetect);
    }
}
