using Kontena.App.ViewModels;
using Kontena.Core.Models;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// The diagnosis block on the pod page (KON-150). The rules themselves are pinned in
/// <c>PodDiagnosisTests</c>; what these cover is the wiring the rules cannot see — that the events
/// are fetched without anyone opening the Events tab, and that the suggestion lands on the logs of
/// the run that ended rather than the one starting.
/// </summary>
public sealed class PodDiagnosisPageTests
{
    private static readonly TerminalFont Font = new("JetBrains Mono", 13, false);

    private static async Task<ClusterPodDetailViewModel> PageFor(string podName)
    {
        var cluster = new FakeClusterEngine();
        var pods = await cluster.ListPodsAsync("app");
        var page = new ClusterPodDetailViewModel(cluster, pods.First(p => p.Name == podName), Font);

        // The page fetches its events on open; the diagnosis follows them.
        for (var i = 0; i < 50 && !page.HasDiagnosis; i++)
            await Task.Delay(10);

        return page;
    }

    [Fact]
    public async Task A_stuck_pod_is_explained_without_opening_the_events_tab()
    {
        // The events used to load only when the tab was selected, which is the tab you go to *because*
        // you could not tell what was wrong.
        using var page = await PageFor("redis-0c1e");

        Assert.True(page.HasDiagnosis);
        Assert.Contains("redis", page.DiagnosisTitle, StringComparison.Ordinal);
        Assert.NotEmpty(page.DiagnosisEvidence);
        Assert.True(page.HasEvents);
    }

    [Fact]
    public async Task Following_a_crash_loop_opens_the_logs_of_the_run_that_ended()
    {
        using var page = await PageFor("redis-0c1e");

        page.FollowDiagnosisCommand.Execute(null);

        Assert.Equal("logs", page.SelectedTab);
        Assert.True(page.ShowPreviousLogs);
    }

    [Fact]
    public async Task A_healthy_pod_carries_no_block_at_all()
    {
        using var page = await PageFor("web-5f2a");

        Assert.False(page.HasDiagnosis);
        Assert.Empty(page.DiagnosisTitle);
    }

    [Fact]
    public async Task The_previous_run_toggle_is_only_offered_where_there_was_one()
    {
        // A container that never restarted has no previous log, and an empty console reads as "it
        // logged nothing" rather than "there is nothing to show".
        using var crashing = await PageFor("redis-0c1e");
        using var healthy = await PageFor("web-5f2a");

        Assert.True(crashing.HasPreviousRun);
        Assert.False(healthy.HasPreviousRun);
    }
}
