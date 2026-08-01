using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;

namespace Kontena.App.Tests;

/// <summary>The resource browser's picker and grid (KON-75).</summary>
public sealed class ClusterResourcesViewModelTests
{
    private static async Task<ClusterResourcesViewModel> PageAsync(string? ns = null)
    {
        var page = new ClusterResourcesViewModel(new FakeClusterEngine(), ns);

        // Discovery and the first listing are started in the constructor; give them their turn.
        for (var i = 0; i < 100 && (page.IsLoadingKinds || page.Table is null); i++)
            await Task.Delay(10);

        return page;
    }

    /// <summary>
    /// Custom kinds go first. The built-in ones largely have a screen of their own already; what someone
    /// opens this page for is the half of the cluster that has none.
    /// </summary>
    [Fact]
    public async Task Custom_kinds_are_listed_before_the_built_in_ones()
    {
        var page = await PageAsync();

        Assert.Equal("Custom resources", page.Groups[0].Title);
        Assert.Contains(page.Groups[0].Items, i => i.Kind == "Certificate");
        Assert.Equal("Kubernetes", page.Groups[1].Title);
    }

    [Fact]
    public async Task The_group_is_shown_so_two_kinds_of_the_same_name_stay_apart()
    {
        var page = await PageAsync();
        var items = page.Groups.SelectMany(g => g.Items).ToArray();

        Assert.Equal("core", Assert.Single(items, i => i.Kind == "Pod").Group);
        Assert.Equal("cert-manager.io", Assert.Single(items, i => i.Kind == "Certificate").Group);
    }

    /// <summary>An empty pane asking to be told what to look at is not a page.</summary>
    [Fact]
    public async Task Opening_the_page_lands_on_a_kind_and_loads_it()
    {
        var page = await PageAsync();

        Assert.NotNull(page.Selected);
        Assert.NotNull(page.Table);
    }

    /// <summary>
    /// The columns are the server's, not the app's — which is the whole reason a kind nobody modelled can
    /// be shown at all.
    /// </summary>
    [Fact]
    public async Task A_custom_kind_arrives_with_the_columns_its_author_declared()
    {
        var page = await PageAsync();
        page.Selected = page.Groups.SelectMany(g => g.Items).First(i => i.Kind == "Certificate");

        for (var i = 0; i < 100 && page.Table?.Columns.Count != 4; i++)
            await Task.Delay(10);

        Assert.Equal(["Name", "Ready", "Secret", "Age"], page.Table!.Columns.Select(c => c.Name));
    }

    [Fact]
    public async Task Filtering_narrows_the_picker_by_kind_and_by_group()
    {
        var page = await PageAsync();

        page.KindSearch = "cert-manager";
        Assert.Equal(["Certificate"], page.Groups.SelectMany(g => g.Items).Select(i => i.Kind));

        page.KindSearch = "conf";
        Assert.Equal(["ConfigMap"], page.Groups.SelectMany(g => g.Items).Select(i => i.Kind));
    }

    /// <summary>
    /// Delete is offered only where the API server says the verb exists. A button that could only ever
    /// fail is worse than no button (KON-117).
    /// </summary>
    [Fact]
    public async Task Delete_is_only_offered_where_the_server_allows_it()
    {
        var page = await PageAsync();
        var items = page.Groups.SelectMany(g => g.Items).ToArray();

        page.Selected = items.First(i => i.Kind == "Certificate");
        Assert.True(page.CanDeleteSelected);

        page.Selected = items.First(i => i.Kind == "Node");
        Assert.False(page.CanDeleteSelected);
    }
}
