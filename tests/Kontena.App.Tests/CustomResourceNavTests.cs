using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;

namespace Kontena.App.Tests;

/// <summary>
/// Custom resources in the sidebar, by API group (KON-483) — the way Lens lists them, rather than only
/// behind a picker on the Resources page.
/// </summary>
public sealed class CustomResourceNavTests
{
    private static async Task<MainWindowViewModel> ShellAsync()
    {
        var shell = new MainWindowViewModel();
        Assert.True(await shell.EnterClusterModeAsync(new FakeClusterEngine()));

        for (var i = 0; i < 100 && shell.NavGroups.All(g => g.Label != "Custom resources"); i++)
            await Task.Delay(10);

        return shell;
    }

    [Fact]
    public async Task Each_custom_api_group_gets_an_entry()
    {
        var shell = await ShellAsync();

        var section = Assert.Single(shell.NavGroups, g => g.Label == "Custom resources");

        // Custom groups only: the fake also serves core and networking.k8s.io kinds.
        Assert.Equal(["resources:cert-manager.io"], section.Items.Select(i => i.Key));
        Assert.Equal("cert-manager.io", section.Items[0].Label);
    }

    [Fact]
    public async Task An_entry_opens_the_resources_page_on_that_group()
    {
        var shell = await ShellAsync();

        shell.NavigateCommand.Execute("resources:cert-manager.io");

        var page = Assert.IsType<ClusterResourcesViewModel>(shell.CurrentPage);
        Assert.Equal("cert-manager.io", page.Group);
        Assert.True(shell.NavGroups.Single(g => g.Label == "Custom resources").Items[0].IsSelected);
    }

    /// <summary>A row opens the generic detail page, through the same route the events feed uses.</summary>
    [Fact]
    public async Task A_row_opens_the_custom_resource_detail()
    {
        var shell = await ShellAsync();
        shell.NavigateCommand.Execute("resources:cert-manager.io");
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
}
