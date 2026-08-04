using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// Deleting the object a detail page is showing (KON-334).
/// <para>
/// Through the shell rather than the page, and that is the whole reason these tests exist separately
/// from <c>DeleteWorkloadServiceIngressTests</c>: a list row's delete only has to remove the object,
/// while a detail's has to leave nothing behind that still points at it — the page itself, and the
/// history step that leads back to it (KON-173).
/// </para>
/// </summary>
public sealed class DeleteFromDetailTests
{
    private static async Task<MainWindowViewModel> ClusterShellAsync(string page, bool watch = true)
    {
        var shell = new MainWindowViewModel();
        Assert.True(await shell.EnterClusterModeAsync(new FakeClusterEngine(watch: watch)));

        shell.NavigateCommand.Execute(page);
        await SettleAsync(shell);

        return shell;
    }

    /// <summary>
    /// Wait for the page to be there and loaded. Cluster pages are rebuilt asynchronously — a delete
    /// asks for the counts before it decides which page to build — and the rows load fire-and-forget
    /// on top of that.
    /// </summary>
    private static async Task SettleAsync(MainWindowViewModel shell)
    {
        for (var i = 0; i < 200 && shell.CurrentPage is not IListPage { HasLoaded: true }; i++)
            await Task.Delay(5);
    }

    private static ConfirmViewModel Asked(MainWindowViewModel shell) =>
        Assert.IsType<ConfirmViewModel>(shell.Dialog);

    private static Task ConfirmAsync(MainWindowViewModel shell) =>
        Asked(shell).ConfirmCommand.ExecuteAsync(null);

    /// <summary>Open the first workload of a per-kind page in the drawer.</summary>
    private static async Task<MainWindowViewModel> WorkloadDetailAsync(WorkloadKind kind, bool watch = true)
    {
        var shell = await ClusterShellAsync(WorkloadNavGroups.KeyFor(kind), watch);

        Assert.IsType<ClusterWorkloadsViewModel>(shell.CurrentPage).Items[0].OpenCommand.Execute(null);
        Assert.IsType<ClusterWorkloadDetailViewModel>(shell.Detail);

        return shell;
    }

    [Fact]
    public async Task A_workload_detail_offers_a_delete_and_asks_before_it_runs()
    {
        var shell = await WorkloadDetailAsync(WorkloadKind.Deployment);
        var detail = Assert.IsType<ClusterWorkloadDetailViewModel>(shell.Detail);

        Assert.True(detail.CanDelete);
        detail.DeleteCommand.Execute(null);

        Assert.True(Asked(shell).Destructive);

        // Asking is all it did: the page is still open on an object that still exists.
        Assert.True(shell.IsDetailOpen);
    }

    [Fact]
    public async Task Confirming_from_the_drawer_deletes_it_and_closes_the_drawer()
    {
        var shell = await WorkloadDetailAsync(WorkloadKind.Deployment);
        var name = Assert.IsType<ClusterWorkloadDetailViewModel>(shell.Detail).Name;

        Assert.IsType<ClusterWorkloadDetailViewModel>(shell.Detail).DeleteCommand.Execute(null);
        await ConfirmAsync(shell);

        await SettleAsync(shell);
        Assert.False(shell.IsDetailOpen);

        shell.NavigateCommand.Execute(WorkloadNavGroups.KeyFor(WorkloadKind.Deployment));
        await SettleAsync(shell);

        Assert.DoesNotContain(
            Assert.IsType<ClusterWorkloadsViewModel>(shell.CurrentPage).Items, r => r.Name == name);
    }

    [Fact]
    public async Task A_cluster_that_cannot_watch_still_gets_its_drawer_closed()
    {
        // Every other test here runs against a cluster that watches, where IsSourceGone (KON-308)
        // would shut the drawer on its own — so on its own none of them can tell whether the delete
        // closes anything. A backend without watch streams sends no Deleted event at all, which is a
        // real backend and not a broken one, and the drawer still has to go.
        var shell = await WorkloadDetailAsync(WorkloadKind.Deployment, watch: false);

        Assert.IsType<ClusterWorkloadDetailViewModel>(shell.Detail).DeleteCommand.Execute(null);
        await ConfirmAsync(shell);

        await SettleAsync(shell);
        Assert.False(shell.IsDetailOpen);
    }

    [Fact]
    public async Task The_detail_says_the_same_thing_the_list_row_says()
    {
        // One wording, two callers (ClusterDeleteWording). The StatefulSet clause is the one worth
        // pinning: it is the sentence that stops a safe delete from looking like a data loss.
        var shell = await WorkloadDetailAsync(WorkloadKind.StatefulSet);

        Assert.IsType<ClusterWorkloadDetailViewModel>(shell.Detail).DeleteCommand.Execute(null);

        Assert.Equal("Delete StatefulSet", Asked(shell).Title);
        Assert.Contains("volume claims", Asked(shell).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleting_from_a_full_page_leaves_the_list_rather_than_a_page_about_nothing()
    {
        var shell = await WorkloadDetailAsync(WorkloadKind.Deployment);

        shell.OpenDetailAsPageCommand.Execute(null);
        var detail = Assert.IsType<ClusterWorkloadDetailViewModel>(shell.CurrentPage);

        detail.DeleteCommand.Execute(null);
        await ConfirmAsync(shell);

        // The page describing it cannot stay up.
        await SettleAsync(shell);
        Assert.IsType<ClusterWorkloadsViewModel>(shell.CurrentPage);

        // And Back must never lead back to it. The step only becomes reachable once something else
        // is navigated to — that is what pushes "where I was" onto the stack — so a Back pressed
        // straight after the delete would pass this whether or not the step was ever dropped
        // (KON-173).
        shell.NavigateCommand.Execute("pods");
        await SettleAsync(shell);

        while (shell.CanGoBack)
        {
            shell.GoBackCommand.Execute(null);
            Assert.IsNotType<ClusterWorkloadDetailViewModel>(shell.CurrentPage);
        }
    }

    [Fact]
    public async Task A_service_detail_deletes_the_service_it_is_showing()
    {
        var shell = await ClusterShellAsync("services");

        Assert.IsType<ClusterServicesViewModel>(shell.CurrentPage).Items[0].OpenCommand.Execute(null);
        var detail = Assert.IsType<ClusterServiceDetailViewModel>(shell.Detail);

        Assert.True(detail.CanDelete);
        detail.DeleteCommand.Execute(null);

        Assert.Equal("Delete service", Asked(shell).Title);
        await ConfirmAsync(shell);

        await SettleAsync(shell);
        Assert.False(shell.IsDetailOpen);
    }

    [Fact]
    public async Task A_pod_detail_deletes_the_pod_it_is_showing()
    {
        var shell = await ClusterShellAsync("pods");

        Assert.IsType<ClusterPodsViewModel>(shell.CurrentPage).Items[0].OpenCommand.Execute(null);
        var detail = Assert.IsType<ClusterPodDetailViewModel>(shell.Detail);

        Assert.True(detail.CanDelete);
        detail.DeleteCommand.Execute(null);

        Assert.Equal("Delete pod", Asked(shell).Title);
        await ConfirmAsync(shell);

        await SettleAsync(shell);
        Assert.False(shell.IsDetailOpen);
    }

    [Fact]
    public async Task A_secret_detail_deletes_the_secret_it_is_showing_and_names_it_as_one()
    {
        var shell = await ClusterShellAsync("secrets");

        Assert.IsType<ClusterSecretsViewModel>(shell.CurrentPage).Items[0].OpenCommand.Execute(null);
        var detail = Assert.IsType<ClusterConfigDetailViewModel>(shell.Detail);

        Assert.True(detail.CanDelete);
        detail.DeleteCommand.Execute(null);

        Assert.Equal("Delete secret", Asked(shell).Title);
        await ConfirmAsync(shell);

        await SettleAsync(shell);
        Assert.False(shell.IsDetailOpen);
    }

    [Fact]
    public async Task A_node_and_a_namespace_detail_offer_no_delete_at_all()
    {
        // They share the detail page shape, and nothing else. Deleting a Node is not how a node
        // leaves a cluster, and a Namespace delete takes everything in it — neither belongs behind a
        // pill in a header, so the opt-in is a constructor argument they simply do not pass.
        var nodes = await ClusterShellAsync("nodes");
        Assert.IsType<ClusterNodesViewModel>(nodes.CurrentPage).Items[0].OpenCommand.Execute(null);
        Assert.False(Assert.IsType<ClusterNodeDetailViewModel>(nodes.Detail).CanDelete);

        var namespaces = await ClusterShellAsync("namespaces");
        Assert.IsType<ClusterNamespacesViewModel>(namespaces.CurrentPage).Items[0].OpenCommand.Execute(null);
        Assert.False(Assert.IsType<ClusterNamespaceDetailViewModel>(namespaces.Detail).CanDelete);
    }
}
