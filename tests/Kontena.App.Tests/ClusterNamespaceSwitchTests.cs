using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Core.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// Which Workloads page a namespace gets, after switching to it (KON-200).
/// <para>
/// The page is the dashboard when there is more than one kind to summarise and the plain list when
/// there is not (KON-174). That answer arrives with the nav counts, and the switch used to navigate
/// first and count afterwards — so it was decided on the namespace you had just left. Both directions
/// were reported: one kind to several gave the list, several to one gave the dashboard.
/// </para>
/// <para>
/// Driven through the shell rather than through <see cref="WorkloadNavGroups"/>, because the rules
/// there were right the whole time. The order in which they were asked was the bug.
/// </para>
/// </summary>
public sealed class ClusterNamespaceSwitchTests
{
    // FakeClusterEngine's own seed: "app" runs Deployments, a StatefulSet, a Job and a CronJob, and
    // "monitoring" runs one DaemonSet. Exactly the two shapes this is about, and not invented here to
    // suit the test.
    private const string ManyKinds = "app";
    private const string OneKind = "monitoring";

    private static async Task<MainWindowViewModel> ClusterShellAsync()
    {
        var shell = new MainWindowViewModel();
        Assert.True(await shell.EnterClusterModeAsync(new FakeClusterEngine()));

        shell.NavigateCommand.Execute("workloads");
        return shell;
    }

    [Fact]
    public async Task Switching_to_a_namespace_with_several_kinds_opens_the_dashboard()
    {
        var shell = await ClusterShellAsync();

        shell.SelectedNamespace = OneKind;
        shell.SelectedNamespace = ManyKinds;

        Assert.IsType<ClusterWorkloadsDashboardViewModel>(shell.CurrentPage);
    }

    [Fact]
    public async Task Switching_to_a_namespace_with_one_kind_opens_the_list()
    {
        // The other direction of the same report: a dashboard summarising a single card is a page
        // that says less than the list it replaced.
        var shell = await ClusterShellAsync();

        shell.SelectedNamespace = ManyKinds;
        shell.SelectedNamespace = OneKind;

        Assert.IsType<ClusterWorkloadsViewModel>(shell.CurrentPage);
    }

    [Fact]
    public async Task A_per_kind_page_that_the_new_namespace_does_not_have_falls_back_to_workloads()
    {
        // Standing on Jobs in app and moving to monitoring, which runs none: the key survives the
        // switch, the kind does not, and the sidebar entry it belongs to is about to be removed.
        var shell = await ClusterShellAsync();
        shell.SelectedNamespace = ManyKinds;
        shell.NavigateCommand.Execute(WorkloadNavGroups.KeyFor(WorkloadKind.Job));

        shell.SelectedNamespace = OneKind;

        // Workloads, not an empty list of Jobs — and monitoring runs one kind, so that is the list.
        // Title, not just the type: a per-kind page is the same class as the all-kinds one, so
        // asserting the type alone would pass on the very thing this is about.
        var page = Assert.IsType<ClusterWorkloadsViewModel>(shell.CurrentPage);
        Assert.Equal("Workloads", page.Title);
    }

    [Fact]
    public async Task Switching_namespace_on_another_page_leaves_that_page_where_it_was()
    {
        var shell = await ClusterShellAsync();
        shell.NavigateCommand.Execute("pods");

        shell.SelectedNamespace = ManyKinds;

        Assert.IsType<ClusterPodsViewModel>(shell.CurrentPage);
    }
}
