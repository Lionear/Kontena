using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// The find page (KON-458). What matters here is not the matching — that is
/// <see cref="ClusterObjectSearchTests"/> — but that typing does not put a sweep per keystroke on the
/// wire, and that the three empty situations stay apart.
/// </summary>
public sealed class ClusterFindViewModelTests
{
    private static ClusterFindViewModel Page(Action<ResourceRef>? onOpen = null) =>
        new(new FakeClusterEngine(), null) { RequestOpen = onOpen };

    private static async Task<ClusterFindViewModel> SearchedAsync(string term, Action<ResourceRef>? onOpen = null)
    {
        var page = Page(onOpen);
        page.SearchText = term;

        for (var i = 0; i < 200 && !page.HasSwept; i++)
            await Task.Delay(10);

        return page;
    }

    [Fact]
    public async Task Typing_a_name_finds_the_object_whatever_its_type()
    {
        var page = await SearchedAsync("kontena-app-tls");

        Assert.True(page.HasHits);
        Assert.Contains(page.Hits, h => h is { Name: "kontena-app-tls", Kind: "Certificate" });
    }

    /// <summary>
    /// A sweep is one listing per resource type, so running it per keystroke would put a hundred
    /// requests on the wire to answer a term still being typed.
    /// </summary>
    [Fact]
    public async Task Typing_does_not_search_until_it_stops()
    {
        var page = Page();

        page.SearchText = "k";
        page.SearchText = "ko";
        page.SearchText = "kontena-app-tls";

        // Well inside the debounce: nothing should have run yet.
        await Task.Delay(ClusterFindViewModel.DebounceMs / 4);
        Assert.False(page.HasSwept);
        Assert.False(page.IsSearching);

        for (var i = 0; i < 200 && !page.HasSwept; i++)
            await Task.Delay(10);

        Assert.True(page.HasHits);
    }

    /// <summary>Retyping drops what the previous term found rather than piling results up.</summary>
    [Fact]
    public async Task A_new_term_replaces_the_previous_answer()
    {
        var page = await SearchedAsync("kontena-app-tls");
        Assert.True(page.HasHits);

        page.SearchText = "zzz-nothing-is-called-this";
        for (var i = 0; i < 200 && !page.HasSwept; i++)
            await Task.Delay(10);

        Assert.Empty(page.Hits);
        Assert.True(page.NothingFound);
    }

    /// <summary>Three empty situations, and the page has to tell them apart.</summary>
    [Fact]
    public void An_untouched_page_is_idle_rather_than_empty()
    {
        var page = Page();

        Assert.True(page.IsIdle);
        Assert.False(page.NothingFound);
        Assert.False(page.HasHits);
    }

    [Fact]
    public async Task A_search_that_found_nothing_says_so()
    {
        var page = await SearchedAsync("zzz-nothing-is-called-this");

        Assert.False(page.IsIdle);
        Assert.True(page.NothingFound);
    }

    [Fact]
    public async Task Clearing_the_box_returns_the_page_to_idle()
    {
        var page = await SearchedAsync("kontena-app-tls");

        page.SearchText = string.Empty;

        Assert.True(page.IsIdle);
        Assert.False(page.NothingFound);
        Assert.Empty(page.Hits);
    }

    /// <summary>
    /// A sweep over a hundred kinds takes long enough that a bare spinner reads as a hang, so how far
    /// along is on screen.
    /// </summary>
    [Fact]
    public async Task Progress_says_how_much_of_the_cluster_was_searched()
    {
        var page = await SearchedAsync("kontena-app-tls");

        Assert.Equal(page.TotalKinds, page.Searched);
        Assert.Contains($"of {page.TotalKinds} resource types", page.Progress, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clicking_a_hit_hands_the_shell_the_object_to_open()
    {
        ResourceRef? opened = null;
        var page = await SearchedAsync("kontena-app-tls", target => opened = target);

        page.Hits.First(h => h.Name == "kontena-app-tls").OpenCommand.Execute(null);

        Assert.Equal("kontena-app-tls", opened?.Name);
        Assert.Equal("Certificate", opened?.Kind.Kind);
    }

    /// <summary>The shared command-bar box drives this page, so it has to say it searches.</summary>
    [Fact]
    public void The_page_takes_part_in_the_shared_search_box()
    {
        Assert.True(Page() is IListPage { SupportsSearch: true, HasLoaded: true });
    }
}
