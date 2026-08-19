using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// What a namespace switch is allowed to change (KON-414). Not the sidebar: the per-kind entries are
/// the cluster's kinds, so the menu is the same shape in every namespace and nothing moves out from
/// under the pointer on a switch.
/// <para>
/// This deliberately reverses KON-200 for the namespace case. A per-kind page used to be navigated
/// away from when the new namespace ran none of that kind, because the entry it belonged to was about
/// to be removed. The entry stays now, so the page stays and says that this namespace has none —
/// which is the answer the user asked for by clicking it.
/// </para>
/// <para>
/// Driven through the shell rather than through <see cref="WorkloadNavGroups"/>, because what is
/// asked and when is the whole of it.
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

    private static IReadOnlyList<string> NavKeys(MainWindowViewModel shell) =>
        [.. shell.NavGroups.SelectMany(g => g.Items).Select(i => i.Key)];

    [Fact]
    public async Task The_sidebar_keeps_its_shape_across_a_namespace_switch()
    {
        // The report: entries for kinds the new namespace does not run disappeared, and came back on
        // the way back. Both directions here, because a menu that only settles once you return is
        // still a menu that moved.
        var shell = await ClusterShellAsync();
        var whole = NavKeys(shell);

        shell.SelectedNamespace = OneKind;
        Assert.Equal(whole, NavKeys(shell));

        shell.SelectedNamespace = ManyKinds;
        Assert.Equal(whole, NavKeys(shell));
    }

    [Fact]
    public async Task A_kind_the_namespace_does_not_run_keeps_its_entry()
    {
        // "monitoring" runs a DaemonSet and nothing else; Jobs is still a row you can click.
        var shell = await ClusterShellAsync();

        shell.SelectedNamespace = OneKind;

        Assert.Contains(WorkloadNavGroups.KeyFor(WorkloadKind.Job), NavKeys(shell));
    }

    [Fact]
    public async Task A_per_kind_page_stays_open_and_says_the_namespace_is_empty()
    {
        // Standing on Jobs in app and moving to monitoring, which runs none. The page used to be
        // swapped for Workloads (KON-200); now it stays, and the empty state names the namespace so
        // it does not read as "this cluster has no Jobs" or as a load that failed.
        var shell = await ClusterShellAsync();
        shell.SelectedNamespace = ManyKinds;
        shell.NavigateCommand.Execute(WorkloadNavGroups.KeyFor(WorkloadKind.Job));

        shell.SelectedNamespace = OneKind;

        // Title, not just the type: a per-kind page is the same class as the all-kinds one, so
        // asserting the type alone would pass on the very thing this is about.
        var page = Assert.IsType<ClusterWorkloadsViewModel>(shell.CurrentPage);
        Assert.Equal("Jobs", page.Title);
        Assert.Equal($"No Job objects found for namespace {OneKind}.", page.EmptyText);
    }

    [Fact]
    public async Task Switching_to_a_namespace_with_one_kind_keeps_the_dashboard()
    {
        // The other half of "the menu does not change shape": which page Workloads is follows the same
        // rule as whether the submenu is drawn (KON-174), so if the submenu no longer depends on the
        // namespace then neither can the page. The dashboard summarises what the namespace has, which
        // for monitoring is one card and the pods.
        var shell = await ClusterShellAsync();

        shell.SelectedNamespace = OneKind;

        Assert.IsType<ClusterWorkloadsDashboardViewModel>(shell.CurrentPage);
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
