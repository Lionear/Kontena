using Kontena.App.ViewModels;
using Kontena.Sdk.Orchestration.Models;
using Kontena.Core.Models;

namespace Kontena.App.Tests;

/// <summary>
/// The per-kind sub-entries under Workloads (KON-169). The rules are about what the sidebar should
/// <i>not</i> show as much as what it should, so they are pinned rather than left to the nav code.
/// </summary>
public sealed class WorkloadNavGroupsTests
{
    private static Workload Workload(WorkloadKind kind, string name = "x") => new()
    {
        Name = name,
        Namespace = "app",
        Kind = kind,
    };

    [Fact]
    public void Only_kinds_that_exist_get_an_entry()
    {
        // A cluster with three Deployments and one Job should not carry four empty rows for the kinds
        // it happens not to run — an empty nav item is a place the user learns not to click (KON-117).
        var kinds = WorkloadNavGroups.KindsIn(
        [
            Workload(WorkloadKind.Deployment, "a"),
            Workload(WorkloadKind.Deployment, "b"),
            Workload(WorkloadKind.Job, "c"),
        ]);

        Assert.Equal([WorkloadKind.Deployment, WorkloadKind.Job], kinds);
    }

    [Fact]
    public void A_kind_appears_once_however_many_of_it_there_are()
    {
        // Kinds, not kinds-and-counts (KON-396). The counts left the sidebar with the other badges
        // (KON-354), and carrying them here is what kept the submenu asking for every object.
        var kinds = WorkloadNavGroups.KindsIn(
        [
            Workload(WorkloadKind.Deployment, "a"),
            Workload(WorkloadKind.Deployment, "b"),
            Workload(WorkloadKind.CronJob, "c"),
        ]);

        Assert.Equal([WorkloadKind.Deployment, WorkloadKind.CronJob], kinds);
    }

    [Fact]
    public void The_order_is_fixed_rather_than_by_count()
    {
        // Ordering by count would reshuffle the sidebar under the pointer the moment a Job finishes.
        var kinds = WorkloadNavGroups.KindsIn(
        [
            Workload(WorkloadKind.CronJob, "a"),
            Workload(WorkloadKind.Job, "b"),
            Workload(WorkloadKind.Job, "c"),
            Workload(WorkloadKind.Deployment, "d"),
        ]);

        Assert.Equal([WorkloadKind.Deployment, WorkloadKind.Job, WorkloadKind.CronJob], kinds);
    }

    [Fact]
    public void One_kind_is_not_worth_a_submenu()
    {
        // The parent already lists exactly those objects; a single child under it says the same thing
        // twice and adds a row for nothing.
        var kinds = WorkloadNavGroups.KindsIn([Workload(WorkloadKind.Deployment, "a"), Workload(WorkloadKind.Deployment, "b")]);

        Assert.Single(kinds);
        Assert.False(WorkloadNavGroups.ShouldGroup(kinds));
    }

    [Fact]
    public void Two_kinds_are()
    {
        var kinds = WorkloadNavGroups.KindsIn([Workload(WorkloadKind.Deployment), Workload(WorkloadKind.StatefulSet)]);

        Assert.True(WorkloadNavGroups.ShouldGroup(kinds));
    }

    [Fact]
    public void An_empty_cluster_has_no_group()
    {
        Assert.Empty(WorkloadNavGroups.KindsIn([]));
        Assert.False(WorkloadNavGroups.ShouldGroup(WorkloadNavGroups.KindsIn([])));
    }

    [Theory]
    [InlineData(WorkloadKind.Deployment, "workloads:Deployment", "Deployments")]
    [InlineData(WorkloadKind.StatefulSet, "workloads:StatefulSet", "StatefulSets")]
    [InlineData(WorkloadKind.CronJob, "workloads:CronJob", "CronJobs")]
    public void A_kind_round_trips_through_its_nav_key(WorkloadKind kind, string key, string label)
    {
        Assert.Equal(key, WorkloadNavGroups.KeyFor(kind));
        Assert.Equal(label, WorkloadNavGroups.LabelFor(kind));
        Assert.Equal(kind, WorkloadNavGroups.KindOf(key));
    }

    [Theory]
    [InlineData("workloads")]
    [InlineData("pods")]
    [InlineData("workloads:")]
    [InlineData("workloads:Nonsense")]
    public void A_key_that_is_not_a_kind_page_reads_as_none(string key)
    {
        // "workloads" itself must not resolve to a kind, or the all-kinds page would route to whichever
        // one Enum.TryParse happened to accept.
        Assert.Null(WorkloadNavGroups.KindOf(key));
    }

    [Fact]
    public void A_kind_page_the_new_kinds_do_not_have_resolves_to_workloads()
    {
        // Switching namespace keeps the page key, and a namespace without Jobs has no Jobs page
        // (KON-200).
        var kinds = WorkloadNavGroups.KindsIn([Workload(WorkloadKind.DaemonSet)]);

        Assert.Equal("workloads", WorkloadNavGroups.ResolveKey("workloads:Job", kinds));
    }

    [Fact]
    public void A_kind_page_that_still_exists_is_left_alone()
    {
        var kinds = WorkloadNavGroups.KindsIn([Workload(WorkloadKind.Job), Workload(WorkloadKind.DaemonSet)]);

        Assert.Equal("workloads:Job", WorkloadNavGroups.ResolveKey("workloads:Job", kinds));

        // And a key that was never about a kind is nobody's business here.
        Assert.Equal("pods", WorkloadNavGroups.ResolveKey("pods", kinds));
    }
}
