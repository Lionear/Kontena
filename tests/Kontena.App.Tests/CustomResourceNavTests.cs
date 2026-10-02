using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// Custom resources in the sidebar, by API group (KON-483) — the way Lens lists them, rather than only
/// behind a picker on the Resources page.
/// </summary>
public sealed class CustomResourceNavTests
{
    private static async Task<MainWindowViewModel> ShellAsync(FakeClusterEngine? cluster = null)
    {
        var shell = new MainWindowViewModel();
        Assert.True(await shell.EnterClusterModeAsync(cluster ?? new FakeClusterEngine()));

        for (var i = 0; i < 100 && shell.NavGroups.All(g => g.Label != "Custom resources"); i++)
            await Task.Delay(10);

        return shell;
    }

    /// <summary>
    /// A folder per API group with its kinds under it, the menu Freelens has — closed until opened, so
    /// only the groups you look into take up room.
    /// </summary>
    [Fact]
    public async Task Each_api_group_is_a_folder_with_its_kinds_under_it()
    {
        var shell = await ShellAsync();

        var section = Assert.Single(shell.NavGroups, g => g.Label == "Custom resources");

        // Custom groups only: the fake also serves core and networking.k8s.io kinds.
        Assert.Equal(
            ["crd-group:cert-manager.io", "resources:cert-manager.io/Certificate"],
            section.Items.Select(i => i.Key));

        var folder = section.Items[0];
        var kind = section.Items[1];
        Assert.True(folder.IsFolder);
        Assert.False(folder.IsExpanded);
        Assert.True(kind.IsNested);
        Assert.False(kind.IsShown);
    }

    [Fact]
    public async Task Clicking_a_folder_opens_and_closes_it_without_leaving_the_page()
    {
        var shell = await ShellAsync();
        var before = shell.CurrentPage;
        var section = shell.NavGroups.Single(g => g.Label == "Custom resources");

        shell.NavigateCommand.Execute("crd-group:cert-manager.io");

        Assert.Same(before, shell.CurrentPage);
        Assert.True(section.Items[0].IsExpanded);
        Assert.True(section.Items[1].IsShown);

        shell.NavigateCommand.Execute("crd-group:cert-manager.io");

        Assert.False(section.Items[0].IsExpanded);
        Assert.False(section.Items[1].IsShown);
    }

    [Fact]
    public async Task A_kind_opens_the_resources_page_on_that_kind_within_its_group()
    {
        var shell = await ShellAsync();

        shell.NavigateCommand.Execute("resources:cert-manager.io/Certificate");

        var page = Assert.IsType<ClusterResourcesViewModel>(shell.CurrentPage);
        Assert.Equal("cert-manager.io", page.Group);
        for (var i = 0; i < 100 && page.Selected is null; i++)
            await Task.Delay(10);
        Assert.Equal("Certificate", page.Selected?.Kind);

        // The kind's own list, like Pods: titled by the kind, the group beside it, no picker.
        Assert.True(page.IsSingleKind);
        Assert.Equal("Certificate", page.Title);
        Assert.Equal("cert-manager.io", page.Subtitle);

        // Its entry is selected, and its folder open so the selection is not hidden inside it.
        var section = shell.NavGroups.Single(g => g.Label == "Custom resources");
        Assert.True(section.Items[1].IsSelected);
        Assert.True(section.Items[0].IsExpanded);
    }

    /// <summary>A row opens the generic detail page, through the same route the events feed uses.</summary>
    [Fact]
    public async Task A_row_opens_the_custom_resource_detail()
    {
        var shell = await ShellAsync();
        shell.NavigateCommand.Execute("resources:cert-manager.io/Certificate");
        var page = Assert.IsType<ClusterResourcesViewModel>(shell.CurrentPage);
        for (var i = 0; i < 100 && page.Rows.Count == 0; i++)
            await Task.Delay(10);

        page.OpenDetail(page.Rows[0], "yaml");
        for (var i = 0; i < 100 && shell.Detail is null; i++)
            await Task.Delay(10);

        var detail = Assert.IsType<ClusterCustomResourceDetailViewModel>(shell.Detail);
        Assert.Equal("Certificate", detail.Kind);
        Assert.True(detail.IsYamlSelected);
    }

    /// <summary>
    /// A CRD installed with kubectl or Helm — no apply through Kontena, no reconnect — still gets its
    /// entry, because the section follows the definitions rather than only the two reads it had
    /// (KON-488).
    /// </summary>
    [Fact]
    public async Task A_crd_installed_outside_kontena_appears_without_a_reconnect()
    {
        var cluster = new FakeClusterEngine();
        var shell = await ShellAsync(cluster);

        cluster.InstallCustomResource(new ApiResource
        {
            Kind = new GroupVersionKind("monitoring.coreos.com", "v1", "PrometheusRule"),
            Plural = "prometheusrules", Namespaced = true, Verbs = ["list"], IsCustom = true,
        });

        IEnumerable<string> Keys() =>
            shell.NavGroups.FirstOrDefault(g => g.Label == "Custom resources")?.Items.Select(i => i.Key) ?? [];

        for (var i = 0; i < 300 && !Keys().Contains("crd-group:monitoring.coreos.com"); i++)
            await Task.Delay(10);

        Assert.Equal(
            [
                "crd-group:cert-manager.io", "resources:cert-manager.io/Certificate",
                "crd-group:monitoring.coreos.com", "resources:monitoring.coreos.com/PrometheusRule",
            ],
            Keys());
    }

    /// <summary>
    /// What the watch must not cost: navigating reads discovery no more than it did before (KON-354,
    /// KON-396). The section is redrawn on a definition changing, never on a click.
    /// </summary>
    [Fact]
    public async Task Navigating_does_not_read_discovery_again()
    {
        var cluster = new FakeClusterEngine();
        var shell = await ShellAsync(cluster);

        // Past the watch's opening burst, which is one read and settles 400 ms after the open.
        var before = -1;
        for (var i = 0; i < 50 && cluster.CallsTo(nameof(FakeClusterEngine.DiscoverResourcesAsync)) != before; i++)
        {
            before = cluster.CallsTo(nameof(FakeClusterEngine.DiscoverResourcesAsync));
            await Task.Delay(600);
        }

        foreach (var key in new[] { "events", "webhooks", "events", "helm" })
            shell.NavigateCommand.Execute(key);
        await Task.Delay(600);

        Assert.Equal(before, cluster.CallsTo(nameof(FakeClusterEngine.DiscoverResourcesAsync)));
    }
}
