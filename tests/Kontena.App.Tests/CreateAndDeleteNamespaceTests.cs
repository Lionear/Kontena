using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// Creating and deleting a namespace from the namespaces page (KON-464). The list was read-only, so
/// the only way to get one was to apply a manifest that declared it — a strange price for an object
/// whose whole content is its name.
/// <para>
/// Both halves go through the primitives the OAL already has: <c>ApplyAsync</c> writes it and
/// <c>DeleteAsync</c> removes it, the same two calls every other cluster page uses. What is new is the
/// name check before the apply and the wording before the delete.
/// </para>
/// </summary>
public sealed class CreateAndDeleteNamespaceTests
{
    private static CreateNamespaceViewModel Modal(
        FakeClusterEngine cluster, Action? onClose = null, Func<Task>? onCreated = null) =>
        new(cluster, onClose ?? (() => { }), onCreated ?? (() => Task.CompletedTask));

    private static async Task<ClusterNamespacesViewModel> PageAsync(FakeClusterEngine cluster)
    {
        var page = new ClusterNamespacesViewModel(cluster);
        await page.LoadAsync();
        return page;
    }

    // ── The modal's name check ───────────────────────────────────────────────

    [Fact]
    public void An_empty_name_cannot_be_created_and_is_not_complained_about_yet()
    {
        var modal = Modal(new FakeClusterEngine());

        Assert.False(modal.CanCreate);

        // Nothing was typed, so there is nothing to be wrong: a form that objects before it has been
        // filled in is noise rather than help.
        Assert.Null(modal.NameProblem);
        Assert.False(modal.HasNameProblem);
    }

    [Theory]
    [InlineData("Staging", "lowercase")]
    [InlineData("my namespace", "letters")]
    [InlineData("-staging", "starting")]
    [InlineData("staging-", "starting")]
    [InlineData("stag.ing", "letters")]
    [InlineData("under_score", "letters")]
    public void A_name_that_is_not_a_dns_label_is_refused_and_says_which_rule_it_broke(
        string name, string expected)
    {
        var modal = Modal(new FakeClusterEngine());
        modal.Name = name;

        Assert.False(modal.CanCreate);
        Assert.True(modal.HasNameProblem);
        Assert.Contains(expected, modal.NameProblem!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_longer_than_a_dns_label_is_refused_by_length()
    {
        var modal = Modal(new FakeClusterEngine());
        modal.Name = new string('a', CreateNamespaceViewModel.MaxLength + 1);

        Assert.False(modal.CanCreate);
        Assert.Contains("63 characters", modal.NameProblem!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("staging")]
    [InlineData("team-a")]
    [InlineData("a")]
    [InlineData("app123")]
    public void A_valid_label_can_be_created(string name)
    {
        var modal = Modal(new FakeClusterEngine());
        modal.Name = name;

        Assert.True(modal.CanCreate);
        Assert.Null(modal.NameProblem);
    }

    [Fact]
    public void Fixing_the_name_clears_the_complaint()
    {
        var modal = Modal(new FakeClusterEngine());
        modal.Name = "Staging";
        Assert.True(modal.HasNameProblem);

        modal.Name = "staging";

        Assert.False(modal.HasNameProblem);
        Assert.True(modal.CanCreate);
    }

    // ── Create ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Creating_puts_the_namespace_in_the_cluster_under_the_name_that_was_typed()
    {
        var cluster = new FakeClusterEngine();
        var modal = Modal(cluster);
        modal.Name = "staging";

        await modal.CreateCommand.ExecuteAsync(null);

        Assert.Contains(await cluster.ListNamespacesAsync(), n => n.Name == "staging");
    }

    [Fact]
    public async Task Creating_refreshes_the_list_and_then_closes()
    {
        var cluster = new FakeClusterEngine();
        var refreshed = false;
        var closed = false;
        var modal = Modal(
            cluster,
            onClose: () => closed = true,
            onCreated: () =>
            {
                // The order matters: closing first would leave the page it came from a redraw behind.
                Assert.False(closed);
                refreshed = true;
                return Task.CompletedTask;
            });
        modal.Name = "staging";

        await modal.CreateCommand.ExecuteAsync(null);

        Assert.True(refreshed);
        Assert.True(closed);
    }

    [Fact]
    public async Task A_name_that_was_never_valid_creates_nothing_even_if_the_command_is_run()
    {
        // CanCreate greys the button out; the command guards itself as well, because a disabled button
        // is a rendering choice and this is the actual write.
        var cluster = new FakeClusterEngine();
        var before = (await cluster.ListNamespacesAsync()).Count;
        var modal = Modal(cluster);
        modal.Name = "Not A Label";

        await modal.CreateCommand.ExecuteAsync(null);

        Assert.Equal(before, (await cluster.ListNamespacesAsync()).Count);
    }

    [Fact]
    public async Task The_name_is_trimmed_before_it_is_applied()
    {
        var cluster = new FakeClusterEngine();
        var modal = Modal(cluster);
        modal.Name = "  staging  ";

        await modal.CreateCommand.ExecuteAsync(null);

        Assert.Contains(await cluster.ListNamespacesAsync(), n => n.Name == "staging");
    }

    [Fact]
    public void The_page_asks_the_shell_for_the_modal_rather_than_building_one()
    {
        var asked = false;
        var page = new ClusterNamespacesViewModel(new FakeClusterEngine())
        {
            RequestCreateNamespace = () => asked = true,
        };

        page.CreateNamespaceCommand.Execute(null);

        Assert.True(asked);
    }

    // ── Delete ───────────────────────────────────────────────────────────────

    private static async Task<(ClusterNamespacesViewModel Page, ConfirmRequest Request)> AskedAsync(
        FakeClusterEngine cluster, string name)
    {
        ConfirmRequest? asked = null;
        var page = await PageAsync(cluster);
        page.RequestConfirm = request => asked = request;

        page.Items.First(r => r.Name == name).DeleteCommand.Execute(null);

        Assert.NotNull(asked);
        return (page, asked);
    }

    [Fact]
    public async Task Deleting_a_namespace_asks_first_and_says_the_contents_go_with_it()
    {
        // The clause that decides whether someone clicks. The row shows a name and an age, and the
        // delete takes every object that was ever put in it — kubectl says so, and so should this.
        var (page, request) = await AskedAsync(new FakeClusterEngine(), "app");

        Assert.True(request.Destructive);
        Assert.Equal("Delete", request.ConfirmLabel);
        Assert.Equal("Delete namespace", request.Title);
        Assert.Contains("app", request.Message, StringComparison.Ordinal);
        Assert.Contains("Everything in it goes with it", request.Message, StringComparison.Ordinal);
        Assert.Contains("secrets", request.Message, StringComparison.Ordinal);
        Assert.Contains("Terminating", request.Message, StringComparison.Ordinal);

        // The click only asked.
        Assert.Contains(page.Items, r => r.Name == "app");
    }

    [Fact]
    public async Task Confirming_deletes_the_namespace_and_the_list_agrees_afterwards()
    {
        var cluster = new FakeClusterEngine();
        var (page, request) = await AskedAsync(cluster, "app");

        await request.OnConfirm();

        Assert.DoesNotContain(page.Items, r => r.Name == "app");
        Assert.DoesNotContain(await cluster.ListNamespacesAsync(), n => n.Name == "app");
    }

    [Fact]
    public async Task The_delete_addresses_the_row_it_was_clicked_on_and_names_no_namespace_of_its_own()
    {
        // Namespaces are cluster-scoped, so a reference carrying one would be addressing something
        // that does not exist.
        var page = await PageAsync(new FakeClusterEngine());

        Assert.Equal(
            new ResourceRef(GroupVersionKind.Namespace, null, "monitoring"),
            page.Items.First(r => r.Name == "monitoring").Reference);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("kube-system")]
    public async Task The_namespaces_the_cluster_needs_are_not_offered_a_delete(string name)
    {
        // A button that is always refused, or that breaks the cluster when it is not, is worse than
        // no button.
        var page = await PageAsync(new FakeClusterEngine());

        Assert.False(page.Items.First(r => r.Name == name).CanDelete);
        Assert.True(page.Items.First(r => r.Name == "app").CanDelete);
    }

    [Fact]
    public async Task A_page_nobody_wired_a_confirm_to_deletes_nothing()
    {
        // The rule from ViewModelBase: no handler, no delete — never a confirm quietly turning into
        // the act it was meant to guard.
        var cluster = new FakeClusterEngine();
        var page = await PageAsync(cluster);

        page.Items.First(r => r.Name == "app").DeleteCommand.Execute(null);
        await Task.Delay(20);

        Assert.Contains(await cluster.ListNamespacesAsync(), n => n.Name == "app");
    }
}
