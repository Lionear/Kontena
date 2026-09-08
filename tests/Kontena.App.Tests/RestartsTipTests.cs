using Kontena.App.ViewModels;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// What the amber RESTARTS count says on hover (KON-442, KON-443). The count alone cannot tell a pod
/// with a past from one with a problem — "8 restarts" and "8 restarts in the last hour" are the same
/// number — so the moment of the last one is the whole point of the sentence.
/// </summary>
public class RestartsTipTests
{
    private static Pod Pod(int restarts, DateTimeOffset? lastRestart) => new()
    {
        Name = "web-5f2b",
        Namespace = "app",
        Phase = PodPhase.Running,
        Restarts = restarts,
        Containers =
        [
            new ContainerStatus
            {
                Name = "web",
                Ready = true,
                Restarts = restarts,
                RunState = ContainerRunState.Running,
                LastTerminationTime = lastRestart,
            },
        ],
    };

    [Fact]
    public void The_moment_of_the_last_restart_is_what_the_count_cannot_say()
    {
        var tip = Format.RestartsTip(Pod(8, DateTimeOffset.UtcNow.AddMinutes(-4)));

        Assert.Contains("Restarted 8 times, most recently 4 min ago.", tip, StringComparison.Ordinal);
    }

    [Fact]
    public void A_restart_long_ago_reads_differently_from_one_just_now()
    {
        // The distinction the ticket exists for: same count, different news.
        var recent = Format.RestartsTip(Pod(8, DateTimeOffset.UtcNow.AddMinutes(-4)));
        var stale = Format.RestartsTip(Pod(8, DateTimeOffset.UtcNow.AddDays(-21)));

        Assert.NotEqual(recent, stale);
        Assert.Contains("21 days ago", stale, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_restart_time_it_says_only_what_it_knows()
    {
        // Every adapter but Kubernetes lands here, and so does a Kubernetes pod whose containers have
        // not actually restarted. Inventing "just now" from a missing value would be worse than silence.
        var tip = Format.RestartsTip(Pod(8, null));

        Assert.StartsWith("Restarted 8 times. ", tip, StringComparison.Ordinal);
        Assert.DoesNotContain("most recently", tip, StringComparison.Ordinal);
    }

    [Fact]
    public void It_always_closes_by_saying_the_pod_is_fine()
    {
        // The reason the tooltip exists at all: the amber has to be readable as "worth a look", never
        // as "something is wrong". Callers guard that this is only shown for a healthy pod.
        Assert.EndsWith(
            "Running normally now — the count is history, not a fault.",
            Format.RestartsTip(Pod(8, null)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Only_containers_that_restarted_contribute_a_moment()
    {
        // A terminated last state without the count agreeing would be a restart time for a restart
        // that never happened.
        var pod = Pod(0, DateTimeOffset.UtcNow.AddMinutes(-4));

        Assert.Null(pod.LastRestart);
    }

    [Fact]
    public void The_pod_reports_the_most_recent_restart_across_its_containers()
    {
        var newest = DateTimeOffset.UtcNow.AddMinutes(-2);
        var pod = Pod(3, DateTimeOffset.UtcNow.AddHours(-9)) with
        {
            InitContainers =
            [
                new ContainerStatus
                {
                    Name = "wait-for-db",
                    Restarts = 1,
                    RunState = ContainerRunState.Terminated,
                    LastTerminationTime = newest,
                },
            ],
        };

        Assert.Equal(newest, pod.LastRestart);
    }
}
