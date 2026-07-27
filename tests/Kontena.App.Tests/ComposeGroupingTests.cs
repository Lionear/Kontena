using Kontena.App.ViewModels;
using Kontena.Engines.Fakes;

namespace Kontena.App.Tests;

/// <summary>
/// Compose projects as one row in the Containers grid (KON-159). These cover the four places a feature
/// like this goes wrong, named on the ticket before a line was written: search, expansion surviving a
/// reload, the stat cards, and where an ungrouped container ends up.
/// </summary>
/// <remarks>
/// Driven against <see cref="FakeEngine"/>'s own seed rather than a scenario invented here: it already
/// holds two projects and six loose containers whose names interleave alphabetically, which is exactly
/// the shape the sorting rule is about. A fixture written to suit the test would have agreed with it.
/// </remarks>
public class ComposeGroupingTests
{
    private static async Task<ContainersViewModel> PageAsync(bool grouped = true)
    {
        var page = new ContainersViewModel(new FakeEngine()) { LoadGrouping = () => grouped };
        await page.LoadAsync();
        return page;
    }

    /// <summary>Rows as they are drawn, with a group shown in brackets.</summary>
    private static List<string> Names(ContainersViewModel page) =>
        [.. page.Items.Select(r => r switch
        {
            ComposeGroupRowViewModel g => $"[{g.Name}]",
            ContainerRowViewModel c => c.Name,
            _ => "?",
        })];

    private static ComposeGroupRowViewModel Group(ContainersViewModel page, string name) =>
        page.Items.OfType<ComposeGroupRowViewModel>().First(g => g.Name == name);

    [Fact]
    public async Task A_project_arrives_as_one_shut_row()
    {
        // Shut to begin with: the point of grouping is that a stack takes one line instead of four.
        // Twelve containers, eight rows.
        var page = await PageAsync();

        Assert.Equal(
            [
                "api-gateway", "[ashenmoon-stack]", "migrate-db", "[monitoring]",
                "postgres-main", "redis-cache", "sqlx-postgres-dev", "worker-jobs",
            ],
            Names(page));
    }

    [Fact]
    public async Task Opening_a_project_puts_its_containers_under_it()
    {
        var page = await PageAsync();

        Group(page, "ashenmoon-stack").ToggleCommand.Execute(null);

        Assert.Equal(
            [
                "api-gateway",
                "[ashenmoon-stack]",
                "ashenmoon-stack-api-1", "ashenmoon-stack-db-1", "ashenmoon-stack-gateway-1",
                "ashenmoon-stack-redis-1",
                "migrate-db", "[monitoring]",
                "postgres-main", "redis-cache", "sqlx-postgres-dev", "worker-jobs",
            ],
            Names(page));
    }

    [Fact]
    public async Task A_group_sits_where_its_name_sits_rather_than_at_the_top()
    {
        // Point 4 of the ticket. "Groups first, then the rest" would move every loose container the
        // moment somebody starts a stack — a list that rearranges itself is one you stop trusting.
        var rows = Names(await PageAsync());

        Assert.True(rows.IndexOf("api-gateway") < rows.IndexOf("[ashenmoon-stack]"));
        Assert.True(rows.IndexOf("[ashenmoon-stack]") < rows.IndexOf("migrate-db"));
        Assert.True(rows.IndexOf("migrate-db") < rows.IndexOf("[monitoring]"));
        Assert.True(rows.IndexOf("[monitoring]") < rows.IndexOf("postgres-main"));
    }

    [Fact]
    public async Task The_summary_counts_what_is_up()
    {
        var page = await PageAsync();

        Assert.Equal("4 of 4 running", Group(page, "ashenmoon-stack").SummaryText);

        // grafana exited, prometheus did not — the fraction is the point of the row.
        Assert.Equal("1 of 2 running", Group(page, "monitoring").SummaryText);
    }

    [Fact]
    public async Task Opening_one_project_leaves_the_others_shut()
    {
        var page = await PageAsync();

        Group(page, "monitoring").ToggleCommand.Execute(null);

        var rows = Names(page);
        Assert.Contains("monitoring-grafana-1", rows);
        Assert.DoesNotContain("ashenmoon-stack-api-1", rows);
    }

    [Fact]
    public async Task Expansion_survives_a_reload()
    {
        // Point 2. The list reloads after every action, so a group that collapsed on each start would
        // make the mode unusable — you would be fighting it.
        var page = await PageAsync();
        Group(page, "monitoring").ToggleCommand.Execute(null);

        await page.LoadAsync();

        Assert.Contains("monitoring-grafana-1", Names(page));
        Assert.True(Group(page, "monitoring").IsOpen);
    }

    [Fact]
    public async Task A_search_that_hits_a_child_opens_its_group()
    {
        // Point 1, the one that matters most: a hit inside a collapsed group is a hit nobody sees.
        var page = await PageAsync();

        page.SearchText = "grafana";

        Assert.Equal(["[monitoring]", "monitoring-grafana-1"], Names(page));
    }

    [Fact]
    public async Task Clearing_the_search_puts_the_group_back_the_way_it_was()
    {
        // Forced open by a search is not the same as opened by the user, and the difference has to
        // survive the search being cleared.
        var page = await PageAsync();

        page.SearchText = "grafana";
        page.SearchText = string.Empty;

        Assert.DoesNotContain("monitoring-grafana-1", Names(page));
    }

    [Fact]
    public async Task A_search_on_the_project_name_shows_the_whole_stack()
    {
        var page = await PageAsync();

        page.SearchText = "monitoring";

        // Both containers, not only the ones whose own name happens to contain the query.
        Assert.Equal(["[monitoring]", "monitoring-grafana-1", "monitoring-prometheus-1"], Names(page));
    }

    [Fact]
    public async Task A_group_with_nothing_matching_disappears_rather_than_sitting_there_empty()
    {
        var page = await PageAsync();

        page.SearchText = "sqlx";

        Assert.Equal(["sqlx-postgres-dev"], Names(page));
    }

    [Fact]
    public async Task Closing_a_group_that_a_search_forced_open_actually_closes_it()
    {
        // Otherwise the first click appears to do nothing, which is the dead-button mistake again.
        var page = await PageAsync();
        page.SearchText = "grafana";

        Group(page, "monitoring").ToggleCommand.Execute(null);

        Assert.Equal(["[monitoring]"], Names(page));
    }

    [Fact]
    public async Task Turning_grouping_off_gives_back_the_flat_list()
    {
        var page = await PageAsync(grouped: false);

        Assert.Empty(page.Items.OfType<ComposeGroupRowViewModel>());
        Assert.Contains("monitoring-grafana-1", Names(page));
        Assert.Equal(12, page.Items.Count);
    }

    [Fact]
    public async Task The_choice_is_remembered_for_this_backend()
    {
        bool? saved = null;
        var page = new ContainersViewModel(new FakeEngine())
        {
            LoadGrouping = () => true,
            SaveGrouping = value => saved = value,
        };
        await page.LoadAsync();

        page.ToggleGroupingCommand.Execute(null);

        Assert.False(saved);
        Assert.False(page.IsGrouped);
    }

    [Fact]
    public async Task Restoring_the_stored_choice_does_not_write_it_back()
    {
        // Reading a setting is not a decision. Saving here would rewrite the file on every page open.
        var saves = 0;
        var page = new ContainersViewModel(new FakeEngine())
        {
            LoadGrouping = () => false,
            SaveGrouping = _ => saves++,
        };

        await page.LoadAsync();

        Assert.False(page.IsGrouped);
        Assert.Equal(0, saves);
    }

    [Fact]
    public async Task The_stat_cards_count_containers_and_not_groups()
    {
        // Point 3. "Running 9" has to mean nine containers, however the list happens to be drawn.
        var page = await PageAsync();
        var running = page.RunningCount;
        var stopped = page.StoppedCount;

        Assert.Equal(12, running + stopped);

        page.ToggleGroupingCommand.Execute(null);

        Assert.Equal(running, page.RunningCount);
        Assert.Equal(stopped, page.StoppedCount);
    }

    [Fact]
    public async Task The_sidebar_counts_containers_and_not_rows()
    {
        // Grouping changes how many rows there are. A nav count that moved when you folded a project
        // would be counting the wrong noun — and twelve containers must read as twelve either way.
        var page = await PageAsync();

        Assert.Equal(12, page.ContainerCount);
        Assert.Equal(8, page.Items.Count);

        page.ToggleGroupingCommand.Execute(null);

        Assert.Equal(12, page.ContainerCount);
        Assert.Equal(12, page.Items.Count);
    }

    [Fact]
    public async Task Only_a_child_carries_its_service_name()
    {
        var page = await PageAsync();

        Group(page, "monitoring").ToggleCommand.Execute(null);

        var child = page.Items.OfType<ContainerRowViewModel>().First(c => c.Name == "monitoring-grafana-1");
        var loose = page.Items.OfType<ContainerRowViewModel>().First(c => c.Name == "api-gateway");

        Assert.True(child.IsChild);
        Assert.Equal("grafana", child.Service);
        Assert.False(loose.IsChild);
        Assert.Null(loose.Service);
    }

    [Fact]
    public async Task A_container_stops_being_a_child_when_grouping_goes_off()
    {
        var page = await PageAsync();
        page.ToggleGroupingCommand.Execute(null);

        Assert.All(page.Items.OfType<ContainerRowViewModel>(), c => Assert.False(c.IsChild));
    }

    [Fact]
    public void The_down_inventory_names_the_networks_and_leaves_volumes_out()
    {
        // The mockup listed volumes among what disappears. DownProjectAsync does not remove them —
        // containers and the project's Compose networks go, volumes and images stay, exactly like
        // `docker compose down`. A dialog that promises a deletion that never happens is worse than
        // one that says less.
        var details = ComposeProjectsViewModel.ProjectDownDetails(
            ["web", "db"], ["azuriom_default"]);

        Assert.Collection(details,
            d => Assert.Equal(("2 containers", "web, db"), (d.Headline, d.Detail)),
            d => Assert.Equal(("1 network", "azuriom_default"), (d.Headline, d.Detail)));
    }

    [Fact]
    public void A_project_with_no_networks_gets_no_network_line()
    {
        // "0 networks" is noise in a list whose whole job is to be counted.
        var only = Assert.Single(ComposeProjectsViewModel.ProjectDownDetails(["web"], []));

        Assert.Equal("1 container", only.Headline);
    }

    [Fact]
    public async Task Taking_a_project_down_asks_first_and_says_what_survives()
    {
        // Point 5: the widest removal in the app goes through the same confirm as everything else,
        // with the same wording the Projects page uses.
        ConfirmRequest? asked = null;
        var engine = new FakeEngine();
        var page = new ContainersViewModel(engine)
        {
            LoadGrouping = () => true,
            RequestConfirm = request => asked = request,
        };
        await page.LoadAsync();

        await page.ConfirmDownAsync(Group(page, "monitoring"));

        Assert.NotNull(asked);
        Assert.True(asked!.Destructive);
        Assert.Contains("monitoring", asked.Title, StringComparison.Ordinal);
        Assert.Contains("Volumes and images stay", asked.Message, StringComparison.Ordinal);

        // What goes is counted, and named by service rather than by container name (KON-162).
        var containers = Assert.Single(asked.Details!, d => d.Headline == "2 containers");
        Assert.Equal("grafana, prometheus", containers.Detail);

        // Volumes are deliberately absent: Down does not remove them, and a line here would promise
        // a deletion that never happens.
        Assert.DoesNotContain(asked.Details!, d => d.Headline.Contains("volume", StringComparison.Ordinal));

        // Nothing goes until it is confirmed.
        Assert.Equal(12, (await engine.ListContainersAsync()).Count);

        await asked.OnConfirm();

        Assert.DoesNotContain("[monitoring]", Names(page));
        Assert.DoesNotContain("monitoring-grafana-1", Names(page));
    }
}
