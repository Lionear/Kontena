using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// Deleting a workload, a service or an ingress from its own page (KON-332). Until this existed the
/// only way to remove any of them was the generic resource browser — the delete worked, it was just
/// nowhere near the list you were looking at.
/// <para>
/// As with config maps and secrets (KON-253) the call is one line and the wording is the feature: the
/// three kinds break three different things, and a confirm that says "this cannot be undone" and
/// nothing else leaves the reader to guess which.
/// </para>
/// </summary>
public sealed class DeleteWorkloadServiceIngressTests
{
    private static async Task<(TPage Page, ConfirmRequest Request)> AskedAsync<TPage>(
        TPage page, Func<TPage, Task> click)
        where TPage : ViewModelBase
    {
        ConfirmRequest? asked = null;
        page.RequestConfirm = request => asked = request;
        await click(page);

        Assert.NotNull(asked);
        return (page, asked);
    }

    private static async Task<(ClusterWorkloadsViewModel Page, ConfirmRequest Request)> WorkloadAsync(string name) =>
        await AskedAsync(
            new ClusterWorkloadsViewModel(new FakeClusterEngine(), "app"),
            async page =>
            {
                await page.LoadAsync();
                page.Items.First(r => r.Name == name).DeleteCommand.Execute(null);
            });

    private static async Task<(ClusterServicesViewModel Page, ConfirmRequest Request)> ServiceAsync(string name) =>
        await AskedAsync(
            new ClusterServicesViewModel(new FakeClusterEngine(), "app"),
            async page =>
            {
                await page.LoadAsync();
                page.Items.First(r => r.Name == name).DeleteCommand.Execute(null);
            });

    [Fact]
    public async Task Deleting_a_workload_asks_first_and_names_the_kind()
    {
        var (page, request) = await WorkloadAsync("api");

        Assert.True(request.Destructive);
        Assert.Equal("Delete", request.ConfirmLabel);
        Assert.Equal("Delete Deployment", request.Title);
        Assert.Contains("api", request.Message, StringComparison.Ordinal);
        Assert.Contains("app", request.Message, StringComparison.Ordinal);

        // The click only asked.
        Assert.Contains(page.Items, r => r.Name == "api");
    }

    [Fact]
    public async Task Confirming_deletes_the_workload_and_the_list_agrees_afterwards()
    {
        var (page, request) = await WorkloadAsync("api");

        await request.OnConfirm();

        Assert.DoesNotContain(page.Items, r => r.Name == "api");
    }

    [Fact]
    public async Task A_stateful_set_says_its_volume_claims_stay()
    {
        // The clause that decides whether someone clicks: deleting the StatefulSet does not take the
        // data with it, and a confirm implying otherwise stops a delete that was perfectly safe.
        var (_, request) = await WorkloadAsync("postgres");

        Assert.Equal("Delete StatefulSet", request.Title);
        Assert.Contains("volume claims", request.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cron_job_says_the_schedule_stops()
    {
        // A CronJob owns no pods, so "its pods are terminated" would be about something it does not
        // have and silent about the thing it does.
        var (_, request) = await WorkloadAsync("backup");

        Assert.Contains("schedule stops", request.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_service_says_the_pods_survive_and_the_name_stops_resolving()
    {
        // Both halves matter. "Nothing breaks" is wrong, and "this takes the app down" is wrong in the
        // other direction — what actually happens is that healthy pods become unreachable by name.
        var (page, request) = await ServiceAsync("api");

        Assert.Equal("Delete service", request.Title);
        Assert.Contains("keep running", request.Message, StringComparison.Ordinal);
        Assert.Contains("stop resolving", request.Message, StringComparison.Ordinal);

        await request.OnConfirm();
        Assert.DoesNotContain(page.Items, r => r.Name == "api");
    }

    [Fact]
    public async Task A_load_balancer_says_the_external_address_does_not_come_back()
    {
        // The one part of a service delete that re-applying the same manifest does not undo.
        var (_, request) = await ServiceAsync("web");

        Assert.Contains("external address", request.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plain_service_does_not_mention_an_address_it_never_had()
    {
        var (_, request) = await ServiceAsync("api");

        Assert.DoesNotContain("external address", request.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_ingress_says_only_the_way_in_from_outside_goes()
    {
        var (page, request) = await AskedAsync(
            new ClusterIngressesViewModel(new FakeClusterEngine(), "app"),
            async p =>
            {
                await p.LoadAsync();
                p.Items.First().DeleteCommand.Execute(null);
            });

        Assert.Equal("Delete ingress", request.Title);
        Assert.Contains("keep running", request.Message, StringComparison.Ordinal);
        Assert.Contains("from outside", request.Message, StringComparison.Ordinal);

        await request.OnConfirm();
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task A_page_nobody_wired_a_confirm_to_deletes_nothing()
    {
        // The rule from ViewModelBase, restated where it matters most: no handler, no delete — never
        // a confirm quietly turning into the act it was meant to guard.
        var cluster = new FakeClusterEngine();
        var page = new ClusterWorkloadsViewModel(cluster, "app");
        await page.LoadAsync();

        page.Items.First(r => r.Name == "api").DeleteCommand.Execute(null);
        await Task.Delay(20);

        Assert.Contains(await cluster.ListWorkloadsAsync(null, "app"), w => w.Name == "api");
    }

    [Fact]
    public async Task The_delete_addresses_the_row_it_was_clicked_on()
    {
        // A workloads page lists five kinds at once, so the reference cannot come from the page.
        var page = new ClusterWorkloadsViewModel(new FakeClusterEngine(), "app");
        await page.LoadAsync();

        Assert.Equal(
            new ResourceRef(GroupVersionKind.For(WorkloadKind.StatefulSet), "app", "postgres"),
            page.Items.First(r => r.Name == "postgres").Reference);
    }
}
