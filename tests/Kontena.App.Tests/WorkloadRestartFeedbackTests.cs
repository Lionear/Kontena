using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// KON-448: a rollout restart on a fast cluster used to leave nothing behind. The PATCH returns
/// before Kubernetes has moved a pod, so the reload that follows it reads the workload back exactly
/// as it was — and the detail page did not even do that much, because its watch loop ignored
/// everything that was not a Deleted.
/// <para>
/// The two halves are tested apart on purpose. <see cref="RestartTracker"/> is a state machine over
/// readings and can be driven exactly; the page is where a real watch has to arrive and be acted on,
/// which no amount of unit-testing the tracker would prove.
/// </para>
/// </summary>
public sealed class WorkloadRestartFeedbackTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    private static Workload Deployment(string name, RolloutStatus status) => new()
    {
        Name = name, Namespace = "app", Kind = WorkloadKind.Deployment, RolloutStatus = status,
    };

    private static async Task<Workload> ApiAsync(FakeClusterEngine engine) =>
        (await engine.ListWorkloadsAsync(WorkloadKind.Deployment, "app")).First(w => w.Name == "api");

    // ── The tracker ─────────────────────────────────────────────────────────

    [Fact]
    public void A_Complete_reading_before_the_rollout_has_moved_does_not_clear_the_request()
    {
        // The whole reason this class exists. Kubernetes has not touched a pod yet, so the honest
        // reading of the workload is still the one from before the click — and showing it would say
        // "Complete" in green a heartbeat after the user asked for a restart.
        var tracker = new RestartTracker();
        var api = Deployment("api", RolloutStatus.Complete);

        tracker.Requested(api, Now);
        tracker.Observe([api], Now);

        Assert.True(tracker.IsRestarting(api.Reference, Now));
    }

    [Fact]
    public void Once_the_rollout_is_seen_moving_the_clusters_own_status_takes_over()
    {
        var tracker = new RestartTracker();
        var api = Deployment("api", RolloutStatus.Complete);

        tracker.Requested(api, Now);
        tracker.Observe([api with { RolloutStatus = RolloutStatus.Progressing }], Now);

        Assert.False(tracker.IsRestarting(api.Reference, Now));
    }

    [Fact]
    public void A_restart_that_is_seen_through_from_Progressing_to_Complete_is_announced()
    {
        var said = new List<string>();
        var tracker = new RestartTracker(said.Add);
        var api = Deployment("api", RolloutStatus.Complete);

        tracker.Requested(api, Now);
        tracker.Observe([api with { RolloutStatus = RolloutStatus.Progressing }], Now);
        tracker.Observe([api], Now);

        Assert.Equal(["Deployment \"api\" restarted"], said);

        // And only once: the entry is gone, so every later reading of the same workload is just a
        // workload that is Complete.
        tracker.Observe([api], Now);
        Assert.Single(said);
    }

    [Fact]
    public void A_Complete_that_was_never_preceded_by_Progressing_announces_nothing()
    {
        // The counter-proof to the test above, and the reason the flag exists: without it the very
        // first reload after the click would report a restart that had not happened yet.
        var said = new List<string>();
        var tracker = new RestartTracker(said.Add);
        var api = Deployment("api", RolloutStatus.Complete);

        tracker.Requested(api, Now);
        tracker.Observe([api], Now);

        Assert.Empty(said);
    }

    [Fact]
    public void A_request_nothing_ever_confirmed_stops_being_believed()
    {
        // A watch that dropped, or a restart the apiserver ignored. Nothing waits on this timeout —
        // it only stops a row saying "Restarting…" for the rest of the session.
        var tracker = new RestartTracker();
        var api = Deployment("api", RolloutStatus.Complete);

        tracker.Requested(api, Now);

        Assert.True(tracker.IsRestarting(api.Reference, Now.AddMinutes(1)));
        Assert.False(tracker.IsRestarting(api.Reference, Now.AddMinutes(3)));
    }

    [Fact]
    public void A_workload_nobody_restarted_is_never_marked()
    {
        var tracker = new RestartTracker();

        tracker.Requested(Deployment("api", RolloutStatus.Complete), Now);

        Assert.False(tracker.IsRestarting(Deployment("web", RolloutStatus.Complete).Reference, Now));
    }

    // ── The pages ───────────────────────────────────────────────────────────

    [Fact]
    public async Task The_detail_page_follows_its_workload_through_a_rollout()
    {
        // The bug: the page read its Workload once, in the constructor, and the watch loop threw away
        // every Modified event — so the header's rollout pill sat on the reading from before the
        // rollout began, for as long as the page was open.
        var engine = new FakeClusterEngine();
        var api = await ApiAsync(engine);

        using var detail = new ClusterWorkloadDetailViewModel(engine, api);

        Assert.Equal(nameof(RolloutStatus.Complete), detail.RolloutText);

        await engine.RolloutRestartAsync(api.Reference);
        engine.EmitWatchEvent(new ResourceEvent { Type = WatchEventType.Modified, Resource = api.Reference });

        for (var i = 0; i < 200 && detail.RolloutText == nameof(RolloutStatus.Complete); i++)
            await Task.Delay(5);

        Assert.Equal(nameof(RolloutStatus.Progressing), detail.RolloutText);
        Assert.Equal("0", detail.UpToDateText);
    }

    [Fact]
    public async Task The_detail_header_says_Restarting_over_a_status_the_cluster_has_not_moved()
    {
        var engine = new FakeClusterEngine();
        var api = await ApiAsync(engine);
        var tracker = new RestartTracker();

        tracker.Requested(api, DateTimeOffset.UtcNow);

        using var detail = new ClusterWorkloadDetailViewModel(engine, api, restarts: tracker);

        // The cluster still says Complete — that is exactly the reading being overruled.
        Assert.Equal(RolloutStatus.Complete, api.RolloutStatus);
        Assert.Equal(RestartTracker.Restarting, detail.RolloutText);
    }

    [Fact]
    public async Task The_list_row_says_Restarting_until_the_rollout_shows_up_and_then_stops()
    {
        var engine = new FakeClusterEngine();
        var api = await ApiAsync(engine);
        var tracker = new RestartTracker();

        tracker.Requested(api, DateTimeOffset.UtcNow);

        using var page = new ClusterWorkloadsViewModel(
            engine, "app", kind: WorkloadKind.Deployment, restarts: tracker);
        await page.LoadAsync();

        Assert.Equal(RestartTracker.Restarting, page.Items.Single(r => r.Name == "api").Status);

        // Untouched workloads keep their own reading — the counter-proof to a binding that marks
        // every row on the page.
        Assert.Equal(nameof(RolloutStatus.Degraded), page.Items.Single(r => r.Name == "redis").Status);

        // Now the cluster catches up, and the row goes back to answering for itself.
        await engine.RolloutRestartAsync(api.Reference);
        await page.LoadAsync();

        Assert.Equal(nameof(RolloutStatus.Progressing), page.Items.Single(r => r.Name == "api").Status);
    }
}
