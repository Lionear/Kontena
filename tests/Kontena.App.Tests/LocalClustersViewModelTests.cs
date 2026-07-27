using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Core.Orchestration.Provisioning;
using Kontena.Core.Tooling;
using Kontena.Core.Tooling.Fakes;
using Xunit;

namespace Kontena.App.Tests;

public class LocalClustersViewModelTests
{
    /// <summary>
    /// A store rooted in a temp directory, so a test never reads what this machine happens to have
    /// downloaded — the lesson from the sandboxing mistake in the screenshot harness.
    /// </summary>
    private static ManagedToolStore EmptyStore() =>
        new(Path.Combine(Path.GetTempPath(), $"kontena-tests-{Guid.NewGuid():N}"));

    private static LocalClustersViewModel Page(
        FakeClusterProvisioner provisioner,
        Action<string>? show = null,
        Func<Task>? changed = null,
        Func<string, Task<bool>>? use = null,
        string? active = null)
        => new(provisioner, new FakeToolRunner(), store: EmptyStore())
        {
            RequestShowCluster = show,
            RequestClustersChanged = changed,
            RequestUseBackend = use,
            ActiveBackendNow = () => active,
            RequestConfirm = request => _ = request.OnConfirm(),
        };

    [Fact]
    public async Task Loading_reads_the_clusters_and_says_which_tool_is_here()
    {
        var page = Page(new FakeClusterProvisioner().WithCluster("dev"));

        await page.LoadAsync();

        Assert.True(page.CanProvision);
        Assert.Contains("Fake", page.ToolSummary, StringComparison.Ordinal);
        var row = Assert.Single(page.Clusters);
        Assert.Equal("dev", row.Name);
        Assert.Equal("fake-dev", row.Context);
    }

    [Fact]
    public async Task Without_the_tool_the_page_is_about_getting_it_and_nothing_can_be_created()
    {
        var missing = new ToolReadiness(
            new ExternalTool("fake", "fake", ["version"], []), ToolState.Missing, null, null, false, null);

        var page = Page(new FakeClusterProvisioner { Readiness = missing });
        await page.LoadAsync();

        Assert.False(page.CanProvision);
        Assert.True(page.NeedsTooling);
        Assert.False(page.IsEmpty);

        page.NewClusterCommand.Execute(null);
        Assert.True(page.IsList);
        Assert.Null(page.Form);
    }

    [Fact]
    public async Task With_the_tool_but_no_clusters_the_page_offers_to_make_one()
    {
        var page = Page(new FakeClusterProvisioner());
        await page.LoadAsync();

        Assert.True(page.IsEmpty);
        Assert.False(page.NeedsTooling);

        page.NewClusterCommand.Execute(null);
        Assert.True(page.IsForm);
        Assert.NotNull(page.Form);
    }

    [Fact]
    public async Task The_active_backend_is_marked_and_cannot_be_switched_to_again()
    {
        var page = Page(new FakeClusterProvisioner().WithCluster("dev"), active: "kubernetes:fake-dev");
        await page.LoadAsync();

        var row = Assert.Single(page.Clusters);
        Assert.True(row.IsActive);
        Assert.False(row.CanUse);
    }

    [Fact]
    public async Task Creating_streams_the_output_and_ends_with_the_cluster_in_the_list()
    {
        var shown = new List<string>();
        var rebuilds = 0;
        var provisioner = new FakeClusterProvisioner();
        var page = Page(provisioner, show: shown.Add, changed: () => { rebuilds++; return Task.CompletedTask; });

        await page.LoadAsync();
        page.NewClusterCommand.Execute(null);
        page.Form!.Name = "dev";
        page.Form.WorkerNodes = "2";

        await page.CreateCommand.ExecuteAsync(null);

        Assert.True(page.IsList);
        Assert.NotEmpty(page.Output);
        Assert.Single(page.Clusters);
        // With no switch wired, the banner is what is left to offer.
        Assert.True(page.HasCreated);
        Assert.Equal("dev", page.Created!.Name);

        // The spec really carried what the form said.
        Assert.Equal(2, Assert.Single(provisioner.Created).WorkerNodes);

        // Made visible, then the switcher rebuilt — in that order, or the rebuild reads a stale setting.
        Assert.Equal(["kubernetes:fake-dev"], shown);
        Assert.Equal(1, rebuilds);
    }

    [Fact]
    public async Task A_failed_create_keeps_the_output_and_explains_the_shape_it_recognises()
    {
        var provisioner = new FakeClusterProvisioner
        {
            CreateExitCode = 1,
            CreateOutput = ["Preparing nodes", "ERROR: port is already allocated"],
        };

        var page = Page(provisioner);
        await page.LoadAsync();
        page.NewClusterCommand.Execute(null);
        page.Form!.Name = "dev";
        page.Form.Ports[0].HostPort = "8080";
        page.Form.Ports[0].NodePort = "80";

        await page.CreateCommand.ExecuteAsync(null);

        Assert.True(page.IsFailed);
        Assert.Equal(2, page.Output.Count);
        Assert.Empty(page.Clusters);
        Assert.False(page.HasCreated);
    }

    [Fact]
    public async Task After_a_failure_the_form_comes_back_with_what_was_typed()
    {
        var page = Page(new FakeClusterProvisioner { CreateExitCode = 1 });
        await page.LoadAsync();
        page.NewClusterCommand.Execute(null);
        page.Form!.Name = "dev";

        await page.CreateCommand.ExecuteAsync(null);
        page.EditAndRetryCommand.Execute(null);

        Assert.True(page.IsForm);
        Assert.Equal("dev", page.Form.Name);
    }

    [Fact]
    public async Task Deleting_asks_first_and_then_removes_it()
    {
        var provisioner = new FakeClusterProvisioner().WithCluster("dev");
        var rebuilds = 0;
        var page = Page(provisioner, changed: () => { rebuilds++; return Task.CompletedTask; });
        await page.LoadAsync();

        await page.Clusters[0].DeleteCommand.ExecuteAsync(null);

        Assert.Equal(["dev"], provisioner.Deleted);
        Assert.Empty(page.Clusters);
        Assert.Equal(1, rebuilds);
    }

    [Fact]
    public async Task Delete_without_a_confirm_handler_does_nothing_at_all()
    {
        var provisioner = new FakeClusterProvisioner().WithCluster("dev");
        var page = new LocalClustersViewModel(provisioner, new FakeToolRunner(), store: EmptyStore());
        await page.LoadAsync();

        await page.Clusters[0].DeleteCommand.ExecuteAsync(null);

        // KON-126: not wired degrades to nothing, never to a silent forced delete.
        Assert.Empty(provisioner.Deleted);
        Assert.Single(page.Clusters);
    }

    [Fact]
    public async Task A_finished_create_goes_straight_to_the_new_cluster()
    {
        var switched = new List<string>();
        var page = Page(
            new FakeClusterProvisioner(),
            changed: () => Task.CompletedTask,
            use: id => { switched.Add(id); return Task.FromResult(true); });

        await page.LoadAsync();
        page.NewClusterCommand.Execute(null);
        page.Form!.Name = "dev";

        await page.CreateCommand.ExecuteAsync(null);

        Assert.Equal(["kubernetes:fake-dev"], switched);

        // Nothing left to offer: the app is already there, and a banner saying "switch to it" next to
        // the cluster you are looking at is the kind of leftover that teaches people to ignore banners.
        Assert.False(page.HasCreated);
    }

    [Fact]
    public async Task A_cluster_that_is_not_reachable_yet_keeps_the_way_back_to_it()
    {
        var page = Page(
            new FakeClusterProvisioner(),
            use: _ => Task.FromResult(false));

        await page.LoadAsync();
        page.NewClusterCommand.Execute(null);
        page.Form!.Name = "dev";

        await page.CreateCommand.ExecuteAsync(null);

        Assert.True(page.HasCreated);
        Assert.Equal("dev", page.Created!.Name);
    }

    [Fact]
    public async Task Switching_to_a_cluster_asks_the_shell_for_its_backend_id()
    {
        var switched = new List<string>();
        var page = Page(
            new FakeClusterProvisioner().WithCluster("dev"),
            use: id => { switched.Add(id); return Task.FromResult(true); });

        await page.LoadAsync();
        await page.Clusters[0].UseCommand.ExecuteAsync(null);

        Assert.Equal(["kubernetes:fake-dev"], switched);
    }
}
