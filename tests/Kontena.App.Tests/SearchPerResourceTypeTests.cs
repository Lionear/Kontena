using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.Core.Models;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Engines;
using Kontena.Engines.Fakes;
using Kontena.Sdk;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// A search term belongs to the resource type it was typed on, and is still there when you come back
/// to it (KON-426) — unless the connection is set to share one term across all of them.
/// <para>
/// Cluster pages are rebuilt on every visit, so the term has nowhere of its own to live and used to be
/// cleared by every navigation. Engine pages are long-lived fields and always kept theirs; what is new
/// there is the sharing.
/// </para>
/// </summary>
public sealed class SearchPerResourceTypeTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"kontena-search-memory-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    /// <summary>
    /// A shell on a settings file of its own. The store is read when a page is opened, so a shell over
    /// the real settings would answer differently on a machine where the sharing is switched on.
    /// </summary>
    private MainWindowViewModel Shell(bool shareSearch = false)
    {
        var store = new SettingsStore(_path);
        store.Save(new KontenaSettings { ShareSearchAcrossResources = shareSearch });

        return new MainWindowViewModel(new BackendRegistry([]), store, store.Load(), new FakeUpdateService())
        {
            SearchDebounce = TimeSpan.Zero,
        };
    }

    private async Task<MainWindowViewModel> ClusterAsync(bool shareSearch = false)
    {
        var shell = Shell(shareSearch);
        Assert.True(await shell.EnterClusterModeAsync(new FakeClusterEngine()));

        return shell;
    }

    private static async Task<IListPage> OpenAsync(MainWindowViewModel shell, string key)
    {
        shell.NavigateCommand.Execute(key);

        for (var i = 0; i < 400 && shell.CurrentPage is not IListPage { HasLoaded: true }; i++)
            await Task.Delay(5);

        return Assert.IsAssignableFrom<IListPage>(shell.CurrentPage);
    }

    private static string Deployments => WorkloadNavGroups.KeyFor(WorkloadKind.Deployment);

    [Fact]
    public async Task A_resource_type_is_still_searching_for_what_you_left_it_searching_for()
    {
        var shell = await ClusterAsync();

        await OpenAsync(shell, "pods");
        shell.SearchText = "api";
        var filtered = Assert.IsType<ClusterPodsViewModel>(shell.CurrentPage).Items.Count;
        Assert.True(filtered > 0);

        // Away, to something that was never searched: that page opens on everything.
        await OpenAsync(shell, Deployments);
        Assert.Equal(string.Empty, shell.SearchText);
        Assert.Equal(string.Empty, Assert.IsType<ClusterWorkloadsViewModel>(shell.CurrentPage).SearchText);

        var pods = await OpenAsync(shell, "pods");

        // In the box as well as on the page. A filtered list with an empty box is the thing KON-164
        // was actually about, and putting the term back without showing it would be that bug.
        Assert.Equal("api", shell.SearchText);
        Assert.Equal("api", pods.SearchText);
        Assert.Equal(filtered, Assert.IsType<ClusterPodsViewModel>(pods).Items.Count);
    }

    [Fact]
    public async Task Each_resource_type_keeps_its_own_term()
    {
        var shell = await ClusterAsync();

        await OpenAsync(shell, "pods");
        shell.SearchText = "api";

        await OpenAsync(shell, Deployments);
        shell.SearchText = "redis";

        Assert.Equal("api", (await OpenAsync(shell, "pods")).SearchText);
        Assert.Equal("redis", (await OpenAsync(shell, Deployments)).SearchText);
    }

    [Fact]
    public async Task Clearing_the_box_is_remembered_too()
    {
        var shell = await ClusterAsync();

        await OpenAsync(shell, "pods");
        shell.SearchText = "api";
        shell.SearchText = string.Empty;

        await OpenAsync(shell, Deployments);

        // Emptied on purpose is not the same as never searched, and coming back to a term you deleted
        // would be the fix overshooting into its own kind of surprise.
        Assert.Equal(string.Empty, (await OpenAsync(shell, "pods")).SearchText);
    }

    [Fact]
    public async Task Another_connection_starts_from_nothing()
    {
        var shell = await ClusterAsync();

        await OpenAsync(shell, "pods");
        shell.SearchText = "api";

        // A term is about the objects it filtered; the next cluster has other ones.
        Assert.True(await shell.EnterClusterModeAsync(new FakeClusterEngine()));

        Assert.Equal(string.Empty, shell.SearchText);
        Assert.Equal(string.Empty, (await OpenAsync(shell, "pods")).SearchText);
    }

    [Fact]
    public async Task Sharing_carries_the_term_to_the_next_resource_type()
    {
        var shell = await ClusterAsync(shareSearch: true);

        await OpenAsync(shell, "pods");
        shell.SearchText = "redis";

        var deployments = await OpenAsync(shell, Deployments);

        Assert.Equal("redis", shell.SearchText);
        Assert.Equal("redis", deployments.SearchText);
        Assert.Equal("redis", Assert.Single(Assert.IsType<ClusterWorkloadsViewModel>(deployments).Items).Name);
    }

    [Fact]
    public async Task Sharing_reaches_the_engine_pages_as_well()
    {
        // The other half of the app: containers, images, volumes. They keep their own term without any
        // help, so sharing is the only thing this side had to learn.
        var shell = Shell(shareSearch: true);
        await shell.EnterEngineModeAsync(new FakeEngine());

        shell.SearchText = "web";
        var images = await OpenAsync(shell, "images");

        Assert.Equal("web", shell.SearchText);
        Assert.Equal("web", images.SearchText);
    }

    [Fact]
    public void The_setting_is_written_as_soon_as_it_is_switched()
    {
        // Read off the store by the shell when it opens a page, so an unsaved toggle is a toggle that
        // does nothing at all.
        var store = new SettingsStore(_path);
        store.Save(new KontenaSettings());

        var page = new SettingsViewModel(store, store.Load(), []) { ShareSearchAcrossResources = true };

        Assert.True(page.ShareSearchAcrossResources);
        Assert.True(new SettingsStore(_path).Load().ShareSearchAcrossResources);
    }

    [Fact]
    public async Task Without_sharing_the_engine_pages_are_left_alone()
    {
        var shell = Shell();
        await shell.EnterEngineModeAsync(new FakeEngine());

        shell.SearchText = "web";

        Assert.Equal(string.Empty, (await OpenAsync(shell, "images")).SearchText);
        Assert.Equal("web", (await OpenAsync(shell, "containers")).SearchText);
    }
}
