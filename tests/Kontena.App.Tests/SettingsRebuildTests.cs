using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.Core.Models;
using Kontena.Engines;

namespace Kontena.App.Tests;

/// <summary>
/// What survives a settings-page rebuild (KON-76).
/// <para>
/// The page is rebuilt for reasons that have nothing to do with where the user is standing: the demo
/// toggle, a kubeconfig, a cluster being created. Creating a cluster is the one that made this
/// visible — it triggers the rebuild itself, so the create ended by dropping the user back on
/// General, next to a page that was no longer the one they had been reading.
/// </para>
/// </summary>
public sealed class SettingsRebuildTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"kontena-rebuild-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private async Task<MainWindowViewModel> ShellAsync()
    {
        var store = new SettingsStore(_path);
        var settings = new KontenaSettings { Onboarded = true };
        store.Save(settings);

        var vm = new MainWindowViewModel(
            new BackendRegistry([]), store, settings, new FakeUpdateService());

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (vm.SettingsPage is null && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.NotNull(vm.SettingsPage);
        return vm;
    }

    /// <summary>Nudges the shell into a rebuild the way the demo toggle does.</summary>
    private static async Task RebuildAsync(MainWindowViewModel vm)
    {
        var before = vm.SettingsPage;
        vm.SettingsPage!.ShowDemoBackends = !vm.SettingsPage.ShowDemoBackends;

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (ReferenceEquals(vm.SettingsPage, before) && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.False(ReferenceEquals(vm.SettingsPage, before), "the settings page was never rebuilt");
    }

    [Fact]
    public async Task A_rebuild_leaves_the_user_on_the_category_they_were_reading()
    {
        var vm = await ShellAsync();
        vm.SettingsPage!.SelectCategoryCommand.Execute("clusters");

        await RebuildAsync(vm);

        Assert.Equal("clusters", vm.SettingsPage!.Category);
        Assert.True(vm.SettingsPage.IsClusters);
    }

    [Fact]
    public async Task The_local_clusters_page_is_the_same_one_after_a_rebuild()
    {
        var vm = await ShellAsync();
        var page = vm.SettingsPage!.LocalClusters;

        await RebuildAsync(vm);

        // Same instance, or a running create would be streaming into a view model nobody can see —
        // and the console the user is reading would vanish mid-sentence.
        Assert.NotNull(page);
        Assert.Same(page, vm.SettingsPage!.LocalClusters);
    }
}
