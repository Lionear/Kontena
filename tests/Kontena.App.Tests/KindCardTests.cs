using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// The health split on a Workloads dashboard card (KON-174). A card exists to say what the sidebar's
/// count cannot, so what it says has to be true for the kind it is describing.
/// </summary>
public sealed class KindCardTests
{
    private static Workload Workload(WorkloadKind kind, RolloutStatus status, string name = "x") => new()
    {
        Name = name,
        Namespace = "app",
        Kind = kind,
        RolloutStatus = status,
    };

    private static KindCard Card(WorkloadKind kind, params RolloutStatus[] statuses) =>
        new(kind, [.. statuses.Select((s, i) => Workload(kind, s, $"w{i}"))], onOpen: null);

    [Fact]
    public void Only_the_states_that_occur_get_a_legend_entry()
    {
        // Listing every state with a zero beside it turns four facts into a wall, and the split is
        // only worth having if it reads at a glance.
        var card = Card(WorkloadKind.Deployment, RolloutStatus.Complete, RolloutStatus.Complete);

        Assert.Equal(["2 complete"], card.Legend.Select(l => l.Text));
    }

    [Fact]
    public void A_mixed_kind_reports_each_state_once()
    {
        var card = Card(WorkloadKind.Deployment,
            RolloutStatus.Complete, RolloutStatus.Progressing, RolloutStatus.Degraded);

        Assert.Equal(["1 complete", "1 progressing", "1 degraded"], card.Legend.Select(l => l.Text));
    }

    [Fact]
    public void A_CronJob_is_scheduled_rather_than_complete()
    {
        // A CronJob that is not suspended has not completed anything — it is waiting for its next run.
        // "Complete" would claim a run that never happened.
        var card = Card(WorkloadKind.CronJob, RolloutStatus.Complete);

        Assert.Equal(["1 scheduled"], card.Legend.Select(l => l.Text));
        Assert.Equal("schedule", card.Unit);
    }

    [Fact]
    public void A_suspended_CronJob_is_suspended_rather_than_paused()
    {
        Assert.Equal(["1 suspended"], Card(WorkloadKind.CronJob, RolloutStatus.Paused).Legend.Select(l => l.Text));
    }

    [Fact]
    public void A_Job_completes_and_fails_rather_than_being_complete_and_degraded()
    {
        var card = Card(WorkloadKind.Job, RolloutStatus.Complete, RolloutStatus.Degraded);

        Assert.Equal(["1 completed", "1 failing"], card.Legend.Select(l => l.Text));
    }

    [Fact]
    public void The_unit_follows_the_count()
    {
        Assert.Equal("workload", Card(WorkloadKind.Deployment, RolloutStatus.Complete).Unit);
        Assert.Equal("workloads", Card(WorkloadKind.Deployment, RolloutStatus.Complete, RolloutStatus.Complete).Unit);
    }

    [Fact]
    public void The_bar_divides_the_whole_between_the_states()
    {
        var card = Card(WorkloadKind.Deployment,
            RolloutStatus.Complete, RolloutStatus.Complete, RolloutStatus.Degraded, RolloutStatus.Degraded);

        // Star widths, so the four segments always fill exactly the width they are given.
        Assert.Equal(0.5, card.CompleteWidth.Value);
        Assert.Equal(0.5, card.DegradedWidth.Value);
        Assert.Equal(0, card.ProgressingWidth.Value);
        Assert.Equal(0, card.PausedWidth.Value);
    }
}
