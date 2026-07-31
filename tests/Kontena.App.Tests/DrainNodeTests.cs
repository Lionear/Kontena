using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// Node maintenance (KON-251). The drain modal is where a set of decisions about other people's
/// workloads gets reported, so what is pinned here is the reporting: that a refusal is not called a
/// failure, that a partial drain does not read as a finished one, and that stopping says what it
/// left behind.
/// </summary>
public sealed class DrainNodeTests
{
    private static DrainNodeViewModel Drain(string node = "gke-prod-worker-1", Func<Task>? onDone = null) =>
        new(new FakeClusterEngine(), node, onClose: () => { }, onDone: onDone ?? (() => Task.CompletedTask));

    [Fact]
    public async Task Cordoning_comes_first_and_is_reported_as_a_step()
    {
        // The other order leaves a window in which the scheduler puts new work onto the node being
        // emptied, and that is not a race you can win by being quick.
        var page = Drain();

        await page.DrainCommand.ExecuteAsync(null);

        Assert.Equal("Cordoned", page.Steps[0].Status);
        Assert.False(page.Steps[0].IsPod);
    }

    [Fact]
    public async Task A_pod_that_moved_appears_once_and_not_twice()
    {
        // Evicting and Evicted are the same pod. Two rows would make the list a log of state changes
        // rather than a list of pods, and the length would stop meaning anything.
        var page = Drain();

        await page.DrainCommand.ExecuteAsync(null);

        var pods = page.Steps.Where(s => s.IsPod).ToList();
        Assert.Equal(pods.Select(p => (p.Pod, p.Namespace)).Distinct().Count(), pods.Count);
    }

    [Fact]
    public async Task A_disruption_budget_refusing_is_reported_as_a_refusal_and_named()
    {
        // "Drain failed" sends someone to a terminal to find out which budget. The apiserver's own
        // message has the name in it, so it is passed through rather than summarised.
        var page = Drain("gke-prod-worker-2");

        await page.DrainCommand.ExecuteAsync(null);

        var refused = page.Steps.FirstOrDefault(s => s.Status == "Refused");
        Assert.NotNull(refused);
        Assert.Contains("postgres-pdb", refused.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_drain_that_could_not_empty_the_node_does_not_claim_it_did()
    {
        // The one line at the end is the only part most people read.
        var page = Drain("gke-prod-worker-2");

        await page.DrainCommand.ExecuteAsync(null);

        Assert.NotNull(page.Summary);
        Assert.Contains("refused by a disruption budget", page.Summary, StringComparison.Ordinal);
        Assert.Contains("is not empty", page.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_summary_always_says_the_node_is_still_cordoned()
    {
        // A drain leaves the node out of service, and forgetting that is how a cluster quietly runs
        // one node short for a week.
        var page = Drain();

        await page.DrainCommand.ExecuteAsync(null);

        Assert.Contains("cordoned", page.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Draining_twice_is_not_offered()
    {
        var page = Drain();

        Assert.True(page.CanDrain);
        await page.DrainCommand.ExecuteAsync(null);
        Assert.False(page.CanDrain);
    }

    [Fact]
    public async Task The_page_that_started_it_is_told_to_reload()
    {
        // Pod counts and the cordoned marker both changed, and the node list is where they show.
        var reloaded = 0;
        var page = Drain(onDone: () => { reloaded++; return Task.CompletedTask; });

        await page.DrainCommand.ExecuteAsync(null);

        Assert.Equal(1, reloaded);
    }

    [Fact]
    public void Local_scratch_storage_is_off_until_it_is_asked_for()
    {
        // The only option on the dialog that destroys anything.
        Assert.False(Drain().DeleteEmptyDirData);
    }

    // ── The rules themselves ────────────────────────────────────────────────

    [Fact]
    public async Task A_daemonset_pod_is_left_where_it_is_and_told_why()
    {
        // Its controller puts it straight back, so evicting it is a loop rather than a drain.
        var engine = new FakeClusterEngine();
        var steps = new List<DrainProgress>();

        await foreach (var step in engine.DrainNodeAsync("gke-prod-worker-1", new DrainOptions()))
            steps.Add(step);

        var skipped = steps.Where(s => s.Action == DrainAction.Skipped).ToList();
        Assert.Contains(skipped, s => s.Reason.Contains("DaemonSet", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cordoning_a_node_shows_up_on_the_node_itself()
    {
        var engine = new FakeClusterEngine();

        await engine.CordonNodeAsync("gke-prod-worker-1", cordoned: true);
        Assert.True((await engine.ListNodesAsync()).Single(n => n.Name == "gke-prod-worker-1").Unschedulable);

        await engine.CordonNodeAsync("gke-prod-worker-1", cordoned: false);
        Assert.False((await engine.ListNodesAsync()).Single(n => n.Name == "gke-prod-worker-1").Unschedulable);
    }

    [Fact]
    public void The_buttons_are_absent_where_the_backend_cannot_do_it()
    {
        // Offered and then refused is worse than not offered: the affordance is the promise.
        var node = new Node { Name = "worker-1" };

        Assert.False(new NodeCardRow(node).CanMaintain);
        Assert.False(new NodeCardRow(node, canMaintain: false, onCordon: _ => { }).CanMaintain);
        Assert.True(new NodeCardRow(node, canMaintain: true, onCordon: _ => { }).CanMaintain);
    }

    [Fact]
    public void One_button_says_which_direction_it_goes()
    {
        Assert.Equal("Cordon", new NodeCardRow(new Node { Name = "a" }).CordonLabel);
        Assert.Equal("Uncordon", new NodeCardRow(new Node { Name = "a", Unschedulable = true }).CordonLabel);
    }
}
