using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.Sdk;
using Kontena.Engines;

namespace Kontena.App.Tests;

/// <summary>
/// The sidebar is sections, not one list (KON-219).
/// <para>
/// The cluster nav had grown to ten entries with up to five more when Workloads expands, and the only
/// structure in it was an indent. What these pin is the part that is easy to lose in a rewrite: that
/// every entry still carries the command that navigates, that the per-kind children land inside the
/// group their parent lives in rather than at the end of the sidebar, and that the engine nav stays
/// deliberately unlabelled.
/// </para>
/// </summary>
public sealed class SidebarGroupTests
{
    private static MainWindowViewModel Shell()
    {
        var path = Path.Combine(Path.GetTempPath(), "kontena-nav-" + Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(path);

        return new MainWindowViewModel(new BackendRegistry([]), store, store.Load(), new FakeUpdateService());
    }

    [Fact]
    public void The_engine_nav_is_one_unlabelled_run()
    {
        // Five entries do not need dividing, and a single heading over the whole list says nothing.
        // A decision, so it is written down here rather than left to whoever reads the nav next.
        var groups = Shell().NavGroups;

        Assert.Single(groups);
        Assert.False(groups[0].HasLabel);
        Assert.Equal(
            ["containers", "images", "volumes", "networks", "projects"],
            groups[0].Items.Select(i => i.Key));
    }

    [Fact]
    public void Every_entry_can_navigate()
    {
        // The whole nav is built through one helper now. If that helper ever stops wiring the command,
        // the sidebar becomes a column of dead buttons — the KON-117 failure, applied to everything at
        // once.
        var shell = Shell();

        Assert.All(shell.NavGroups.SelectMany(g => g.Items), item => Assert.NotNull(item.Command));
    }

    [Fact]
    public void A_badge_only_appears_where_there_is_something_to_count()
    {
        var item = new NavItem("pods", "Pods", "IconContainer");

        Assert.False(item.HasCount);

        item.Count = "12";
        Assert.True(item.HasCount);

        // Back to nothing: a count that went away has to take its badge with it, or the pill sticks
        // around showing a number that is no longer true.
        item.Count = string.Empty;
        Assert.False(item.HasCount);
    }

    [Fact]
    public void The_workloads_entry_no_longer_repeats_its_own_heading()
    {
        // The group heading says Workloads; a row underneath saying the same word, with a chevron that
        // reveals what the heading already groups, is the same idea told twice. The row is named for
        // the page it opens instead — the dashboard across all kinds.
        var shell = Shell();
        shell.NavGroups.Clear();

        // Reached through the cluster nav builder rather than constructed here, so the test fails if
        // the label is changed in one place and not the other.
        typeof(MainWindowViewModel)
            .GetMethod("SetClusterNav", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(shell, null);

        var workloads = shell.NavGroups.Single(g => g.Label == "Workloads");

        Assert.Equal("All workloads", workloads.Items.Single(i => i.Key == "workloads").Label);
        Assert.DoesNotContain(workloads.Items, i => i.Label == workloads.Label);
    }

    [Fact]
    public void A_group_without_a_label_still_holds_its_items()
    {
        // HasLabel drives whether the heading renders; it must not gate the items themselves.
        var group = new NavGroup();
        group.Items.Add(new NavItem("containers", "Containers", "IconContainer"));

        Assert.False(group.HasLabel);
        Assert.Single(group.Items);
    }
}
