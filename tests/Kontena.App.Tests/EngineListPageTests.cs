using System.Collections.Specialized;
using Kontena.App.ViewModels;
using Kontena.Engines.Fakes;

namespace Kontena.App.Tests;

/// <summary>
/// The engine list pages on the shared list base (KON-189). KON-164 fixed searching on the cluster
/// pages; Images, Networks, Volumes and Projects kept their own <c>Items.Clear()</c>, which is the
/// expensive rebuild the ticket was about, and never learned the difference between "nothing matched"
/// and "this page is empty".
/// <para>
/// Written against the shared behaviour rather than one page: the reconcile and the three states are
/// the same code now, so the test that matters is that every page actually reaches it.
/// </para>
/// </summary>
public class EngineListPageTests
{
    /// <summary>What the bound collection told the view, in order.</summary>
    private sealed class ChangeLog
    {
        public List<NotifyCollectionChangedAction> Actions { get; } = [];

        public ChangeLog(INotifyCollectionChanged source) =>
            source.CollectionChanged += (_, e) => Actions.Add(e.Action);
    }

    private static async Task<T> LoadedAsync<T>(T page) where T : IListPage
    {
        await page.LoadAsync();
        return page;
    }

    [Fact]
    public async Task Narrowing_a_search_keeps_the_rows_that_still_match()
    {
        // The whole point of KON-189: a Clear raises a Reset and every surviving row's visuals are
        // rebuilt with it. Removing what no longer matches touches only what changed.
        var images = await LoadedAsync(new ImagesViewModel(new FakeEngine()));
        images.SearchText = "d";

        var survivor = images.Items.Single(i => i.RepoName == "redis");
        var changes = new ChangeLog(images.Items);

        images.SearchText = "redis";

        Assert.Same(survivor, Assert.Single(images.Items));

        // Removals only. A Reset — which is what Clear() raises — would be the whole list rebuilt,
        // survivor included.
        Assert.NotEmpty(changes.Actions);
        Assert.All(changes.Actions, a => Assert.Equal(NotifyCollectionChangedAction.Remove, a));
    }

    [Fact]
    public async Task Widening_a_search_puts_rows_back_in_their_own_order()
    {
        var volumes = await LoadedAsync(new VolumesViewModel(new FakeEngine()));

        volumes.SearchText = "pgdata";
        var narrowed = volumes.Items.Select(v => v.Name).ToList();

        volumes.SearchText = string.Empty;

        Assert.Equal(["pgdata"], narrowed);
        Assert.Equal(volumes.Items.Select(v => v.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase), volumes.Items.Select(v => v.Name));
    }

    [Fact]
    public async Task A_search_that_matches_nothing_is_not_the_same_as_an_empty_page()
    {
        var networks = await LoadedAsync(new NetworksViewModel(new FakeEngine()));

        networks.SearchText = "no-such-network";

        Assert.True(networks.HasNoMatches);
        Assert.False(networks.IsEmpty);
        Assert.False(networks.HasItems);
    }

    [Fact]
    public async Task An_engine_with_nothing_on_it_reports_an_empty_page()
    {
        var networks = await LoadedAsync(new NetworksViewModel(new FakeEngine(seed: false)));

        Assert.True(networks.IsEmpty);
        Assert.False(networks.HasNoMatches);
    }

    [Fact]
    public async Task Nothing_is_claimed_before_the_first_load()
    {
        // Both states hang off HasLoaded: an empty page and a page that has not answered yet look the
        // same in the collection, and only one of them is worth telling the user about.
        var volumes = new VolumesViewModel(new FakeEngine());

        Assert.False(volumes.IsEmpty);
        Assert.False(volumes.HasNoMatches);

        await volumes.LoadAsync();

        Assert.False(volumes.IsEmpty);
    }

    [Fact]
    public async Task A_reload_under_an_active_search_does_not_quietly_show_everything_again()
    {
        var images = await LoadedAsync(new ImagesViewModel(new FakeEngine()));
        images.SearchText = "redis";

        await images.LoadAsync();

        Assert.Equal(["redis"], images.Items.Select(i => i.RepoName));
    }

    [Fact]
    public async Task Projects_match_on_their_services_as_well_as_their_own_name()
    {
        var projects = await LoadedAsync(new ComposeProjectsViewModel(new FakeEngine()));
        Assert.True(projects.Items.Count > 1);

        var service = projects.Items[0].Services[0].Name;
        projects.SearchText = service;

        Assert.Contains(projects.Items, p => p.Services.Any(s => s.Name == service));
    }
}
