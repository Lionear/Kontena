using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

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

    /// <summary>
    /// The command-bar search box binds to whatever page is showing, but only where the page says it
    /// searches. This page did not, so the box sat there greyed out — the complaint behind KON-454.
    /// </summary>
    [Fact]
    public async Task The_page_takes_part_in_the_shared_search_box()
    {
        var page = await PageAsync();

        Assert.True(page is IListPage { SupportsSearch: true });
        Assert.True(((IListPage)page).HasLoaded);
    }

    [Fact]
    public async Task Searching_narrows_the_listing_to_the_rows_that_match_any_column()
    {
        var page = await CertificatesAsync();
        var all = page.Rows.Count;

        page.SearchText = "kontena-app-tls";

        Assert.Equal(["kontena-app-tls"], page.Rows.Select(r => r.Reference.Name));
        Assert.True(all > page.Rows.Count, "the search should have left rows out");
    }

    /// <summary>A search that matched nothing is a different answer from a kind with nothing in it.</summary>
    [Fact]
    public async Task A_search_that_matches_nothing_says_so_rather_than_looking_empty()
    {
        var page = await CertificatesAsync();

        page.SearchText = "nothing-is-called-this";

        Assert.Empty(page.Rows);
        Assert.True(page.HasNoMatches);
        Assert.False(page.IsEmpty);
    }

    /// <summary>
    /// Three states, the same as every cluster list page since KON-318: ascending, descending, and
    /// back to the order the server sent.
    /// </summary>
    [Fact]
    public async Task Clicking_a_header_sorts_then_flips_then_lets_go()
    {
        var page = await CertificatesAsync();
        var served = page.Rows.Select(r => r.Reference.Name).ToArray();

        page.SortByCommand.Execute("Name");
        Assert.Equal("Name", page.SortColumn);
        Assert.False(page.SortDescending);
        var ascending = page.Rows.Select(r => r.Cells[0]).ToArray();
        Assert.Equal([.. ascending.OrderBy(c => c, StringComparer.OrdinalIgnoreCase)], ascending);

        page.SortByCommand.Execute("Name");
        Assert.True(page.SortDescending);
        Assert.Equal([.. ascending.Reverse()], page.Rows.Select(r => r.Cells[0]));

        page.SortByCommand.Execute("Name");
        Assert.Null(page.SortColumn);
        Assert.Equal(served, page.Rows.Select(r => r.Reference.Name));
    }

    /// <summary>A column this listing does not have is ignored, not thrown — the key comes off a click.</summary>
    [Fact]
    public async Task Sorting_by_a_column_this_kind_does_not_have_is_ignored()
    {
        var page = await CertificatesAsync();

        page.SortByCommand.Execute("NoSuchColumn");

        Assert.Null(page.SortColumn);
    }

    /// <summary>Filter first, then sort: sorting decides the order of what survived the search.</summary>
    [Fact]
    public async Task Sorting_applies_to_what_the_search_left_over()
    {
        var page = await CertificatesAsync();

        page.SortByCommand.Execute("Name");
        page.SearchText = "tls";

        Assert.All(page.Rows, row => Assert.Contains("tls", row.Cells[0], StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<ClusterResourcesViewModel> CertificatesAsync()
    {
        var page = await PageAsync();
        page.Selected = page.Groups.SelectMany(g => g.Items).First(i => i.Kind == "Certificate");

        for (var i = 0; i < 100 && page.Rows.Count == 0; i++)
            await Task.Delay(10);

        return page;
    }

    /// <summary>
    /// The complaint behind KON-455: you know the object is in a custom resource, you do not know what
    /// the type is called. Kind-only matching cannot answer that; the names kubectl accepts can.
    /// </summary>
    [Theory]
    [InlineData("cert")]          // short name
    [InlineData("certificates")]  // plural
    [InlineData("cert-manager")]  // category, and the group
    [InlineData("CERT")]          // and none of it is case-sensitive
    public async Task A_kind_is_found_by_every_name_it_answers_to(string term)
    {
        var page = await PageAsync();

        page.KindSearch = term;

        Assert.Contains(page.Groups.SelectMany(g => g.Items), i => i.Kind == "Certificate");
    }

    /// <summary>A name you can search by but cannot see reads as a search that failed.</summary>
    [Fact]
    public async Task The_names_a_kind_answers_to_are_shown_under_it()
    {
        var page = await PageAsync();
        var certificate = page.Groups.SelectMany(g => g.Items).Single(i => i.Kind == "Certificate");

        Assert.Contains("cert", certificate.Aliases, StringComparison.Ordinal);
        Assert.Contains("certificates", certificate.Aliases, StringComparison.Ordinal);

        // The kind itself is already the line above; repeating it there is noise.
        Assert.DoesNotContain("Certificate", certificate.Aliases, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_term_that_names_nothing_still_matches_nothing()
    {
        var page = await PageAsync();

        page.KindSearch = "zzz-no-such-kind";

        Assert.Empty(page.Groups);
    }

    /// <summary>
    /// The case KON-455 actually described: you remember what the thing does, not what it is called.
    /// "TLS" appears in no name, no alias and no group — only in the description the CRD author wrote.
    /// </summary>
    [Fact]
    public async Task A_kind_is_found_by_what_it_is_for()
    {
        var page = await PageAsync();

        page.KindSearch = "TLS";

        Assert.Equal(["Certificate"], page.Groups.SelectMany(g => g.Items).Select(i => i.Kind));
    }

    [Fact]
    public async Task The_description_and_what_installed_it_are_on_the_row()
    {
        var page = await PageAsync();
        var certificate = page.Groups.SelectMany(g => g.Items).Single(i => i.Kind == "Certificate");

        Assert.True(certificate.HasDescription);
        Assert.Contains("TLS", certificate.Description, StringComparison.Ordinal);
        Assert.Equal("cert-manager.io · cert-manager", certificate.Origin);
    }

    /// <summary>A built-in kind has no definition to read, so it shows the group and nothing else.</summary>
    [Fact]
    public async Task A_built_in_kind_carries_no_description_line()
    {
        var page = await PageAsync();
        var pod = page.Groups.SelectMany(g => g.Items).Single(i => i.Kind == "Pod");

        Assert.False(pod.HasDescription);
        Assert.Equal("core", pod.Origin);
    }

    /// <summary>
    /// The cross-link (KON-455): opening an object says which workloads use it, and clicking one hands
    /// the shell the reference so it opens that workload's own page.
    /// </summary>
    [Fact]
    public async Task Opening_an_object_says_which_workloads_use_it()
    {
        var page = await CertificatesAsync();
        var row = page.Rows.Single(r => r.Reference.Name == "kontena-app-tls");

        await page.ShowManifestAsync(row);
        for (var i = 0; i < 100 && !page.UsersChecked; i++)
            await Task.Delay(10);

        Assert.True(page.HasUsers);
        Assert.False(page.NothingUsesIt);
        Assert.Equal(["kontena-web", "kontena-api"], page.Users.Select(u => u.Name));
    }

    /// <summary>Both directions in one list, and they do not say the same thing.</summary>
    [Fact]
    public async Task A_row_says_how_the_link_was_found_and_which_way_it_runs()
    {
        var page = await UsersOfTlsAsync();

        var mounts = page.Users.Single(u => u.Name == "kontena-web");
        Assert.Equal("mounts Secret kontena-app-tls", mounts.Evidence);
        Assert.Equal("uses this", mounts.Direction);
        Assert.Equal("Deployment · namespace default", mounts.Where);

        var owned = page.Users.Single(u => u.Name == "kontena-api");
        Assert.Equal("ownerReference", owned.Evidence);
        Assert.Equal("created by this", owned.Direction);
    }

    [Fact]
    public async Task Clicking_a_row_hands_the_shell_the_workload_to_open()
    {
        ResourceRef? opened = null;
        var page = await UsersOfTlsAsync(target => opened = target);

        page.Users.Single(u => u.Name == "kontena-web").OpenCommand.Execute(null);

        Assert.Equal("kontena-web", opened?.Name);
        Assert.Equal(GroupVersionKind.Deployment, opened?.Kind);
    }

    /// <summary>
    /// Nothing found is stated, not left blank: a generic ladder will miss a custom resource that links
    /// itself some other way, and silence cannot be told apart from "nothing uses this".
    /// </summary>
    [Fact]
    public async Task An_object_nothing_uses_says_so()
    {
        var page = await CertificatesAsync();
        var row = page.Rows.First(r => r.Reference.Name != "kontena-app-tls");

        await page.ShowManifestAsync(row);
        for (var i = 0; i < 100 && !page.UsersChecked; i++)
            await Task.Delay(10);

        Assert.False(page.HasUsers);
        Assert.True(page.NothingUsesIt);
    }

    /// <summary>Closing the panel drops the answer with it, so the next object starts from nothing.</summary>
    [Fact]
    public async Task Closing_the_panel_forgets_what_used_the_object()
    {
        var page = await UsersOfTlsAsync();

        page.CloseManifestCommand.Execute(null);

        Assert.Empty(page.Users);
        Assert.False(page.NothingUsesIt);
    }

    private static async Task<ClusterResourcesViewModel> UsersOfTlsAsync(Action<ResourceRef>? onOpen = null)
    {
        var page = await CertificatesAsync();
        page.RequestOpen = onOpen;

        await page.ShowManifestAsync(page.Rows.Single(r => r.Reference.Name == "kontena-app-tls"));
        for (var i = 0; i < 100 && !page.UsersChecked; i++)
            await Task.Delay(10);

        return page;
    }
}
