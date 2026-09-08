using Kontena.Adapters.Podman;
using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.Sdk.Models;
using Kontena.Sdk;
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

        // No engine ever created here (KON-306): a rebuild used to always re-probe the real
        // Docker/Podman providers regardless of what triggered it, which made this test transitively
        // depend on the host's engines and, on a Windows runner without Podman, occasionally miss its
        // own deadline.
        var vm = new MainWindowViewModel(
            new BackendRegistry([]), store, settings, new FakeUpdateService(),
            buildCatalog: (_, _, _, _, _) => []);

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

    /// <summary>An install must survive a rebuild for the same reason a create does (KON-266).</summary>
    [Fact]
    public async Task The_tools_page_is_the_same_one_after_a_rebuild()
    {
        var vm = await ShellAsync();
        var page = vm.SettingsPage!.Tools;

        await RebuildAsync(vm);

        Assert.NotNull(page);
        Assert.Same(page, vm.SettingsPage!.Tools);
    }

    /// <summary>
    /// A rebuilt page reads the settings the page it replaces just wrote (KON-430).
    /// <para>
    /// Switching an adapter off writes through immediately and then asks the shell to rebuild. The
    /// shell used to rebuild the switcher from disk but the page from the copy it was holding, which
    /// was a snapshot from before that write — so the switch sprang back to on while settings.json
    /// said off, and clicking it again computed the wrong direction and wrote nothing at all.
    /// </para>
    /// <para>
    /// At shell level on purpose: <c>ExtensionsSettingsTests</c> drives the page with a stand-in for
    /// the rebuild and proves the write is right, which it always was. The defect only exists in the
    /// hand-off.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_rebuilt_page_shows_the_adapter_the_previous_page_just_switched_off()
    {
        var vm = await ShellAsync();
        var before = vm.SettingsPage;
        var row = Assert.Single(vm.SettingsPage!.Adapters, r => r.Id == PodmanAdapterModule.BackendId);
        Assert.True(row.IsEnabled);

        row.IsEnabled = false;

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (ReferenceEquals(vm.SettingsPage, before) && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.False(ReferenceEquals(vm.SettingsPage, before), "the settings page was never rebuilt");

        // Both halves matter. The store proves the click was recorded; the row proves the page the
        // user is now looking at agrees with it. Before KON-430 the first passed and the second did not.
        Assert.False(new SettingsStore(_path).Load().IsAdapterEnabled(PodmanAdapterModule.BackendId));
        Assert.False(
            Assert.Single(vm.SettingsPage!.Adapters, r => r.Id == PodmanAdapterModule.BackendId).IsEnabled);
    }

    /// <summary>
    /// Local clusters points at Tools rather than housing a second copy of it (KON-266). The pointer
    /// is a category switch owned by the settings page, so this is what says it is wired at all.
    /// </summary>
    [Fact]
    public async Task Local_clusters_can_open_the_tools_page()
    {
        var vm = await ShellAsync();
        vm.SettingsPage!.SelectCategoryCommand.Execute("clusters");

        vm.SettingsPage.LocalClusters!.ShowToolsCommand.Execute(null);

        Assert.Equal("tools", vm.SettingsPage.Category);
        Assert.True(vm.SettingsPage.IsTools);
    }
}
