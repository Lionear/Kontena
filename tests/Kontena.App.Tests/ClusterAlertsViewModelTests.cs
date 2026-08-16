using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;
using Xunit;

namespace Kontena.App.Tests;

public class ClusterAlertsViewModelTests
{
    private static async Task<ClusterAlertsViewModel> PageAsync(FakeClusterEngine? cluster = null)
    {
        var page = new ClusterAlertsViewModel(cluster ?? new FakeClusterEngine());

        // The constructor kicks off the load; the assertions want it finished.
        await page.LoadAsync();
        return page;
    }

    private static Alert Alert(
        string name, string severity = "critical", string? pod = null,
        AlertState state = AlertState.Firing, string[]? silencedBy = null, int ageMinutes = 10) =>
        new()
        {
            Labels = new Dictionary<string, string>
            {
                ["alertname"] = name,
                ["severity"] = severity,
                ["namespace"] = "app",
                ["pod"] = pod ?? "p-1",
            },
            State = state,
            StartsAt = DateTimeOffset.UtcNow.AddMinutes(-ageMinutes),
            SilencedBy = silencedBy ?? [],
        };

    [Fact]
    public async Task Instances_of_one_alertname_become_one_group()
    {
        var page = await PageAsync();

        // The whole reason the list is grouped: two crash-looping pods are one problem with two
        // instances, not two problems.
        var crashLoop = Assert.Single(page.Firing, g => g.Name == "KubePodCrashLooping");
        Assert.Equal(2, crashLoop.Count);
        Assert.Equal(2, crashLoop.Instances.Count);
        Assert.Contains(crashLoop.Instances, i => i.Target == "redis-7d9c4f-x2ktp");
        Assert.Contains(crashLoop.Instances, i => i.Target == "worker-5f8b9d-qq4mn");
    }

    [Fact]
    public async Task Firing_pending_and_silenced_are_separate_sections()
    {
        var page = await PageAsync();

        Assert.True(page.HasFiring);
        Assert.True(page.HasPending);
        Assert.True(page.HasSilenced);

        Assert.Equal("HighRequestLatency", Assert.Single(page.Pending).Name);
        Assert.Equal("KubeJobFailed", Assert.Single(page.Silenced).Name);

        // Firing holds the rest, and nothing appears twice.
        Assert.DoesNotContain(page.Firing, g => g.Name is "HighRequestLatency" or "KubeJobFailed");
    }

    [Fact]
    public void One_alertname_can_sit_in_two_sections_when_only_some_instances_are_muted()
    {
        // Two of three pods silenced is exactly this, and folding them together would either hide a
        // firing instance or claim a muted one is still shouting.
        var groups = ClusterAlertsViewModel.Group(
            [
                Alert("KubePodCrashLooping", pod: "a"),
                Alert("KubePodCrashLooping", pod: "b", silencedBy: ["sil-1"]),
            ],
            [],
            [new Silence { Id = "sil-1", Matchers = [], CreatedBy = "rick", Comment = "known", EndsAt = DateTimeOffset.UtcNow.AddHours(1) }]);

        Assert.Equal(2, groups.Count);
        Assert.Single(groups, g => g.Section == AlertSection.Firing && g.Count == 1);
        Assert.Single(groups, g => g.Section == AlertSection.Silenced && g.Count == 1);
    }

    [Fact]
    public void The_group_header_carries_the_sentence()
    {
        var silence = new Silence
        {
            Id = "sil-1",
            Matchers = [],
            CreatedBy = "rick",
            Comment = "migrate job, known, fix in #412",
            EndsAt = DateTimeOffset.UtcNow.AddHours(2),
        };

        var groups = ClusterAlertsViewModel.Group(
            [
                Alert("KubePodCrashLooping", ageMinutes: 90),
                Alert("HighRequestLatency", severity: "warning", state: AlertState.Pending, ageMinutes: 3),
                Alert("KubeJobFailed", severity: "warning", silencedBy: ["sil-1"]),
            ],
            [new AlertRule { Name = "HighRequestLatency", Expr = "x", For = TimeSpan.FromMinutes(10) }],
            [silence]);

        // Firing says how long and how many.
        Assert.Contains("firing", groups.Single(g => g.Section == AlertSection.Firing).Why, StringComparison.Ordinal);

        // Pending counts against the rule's `for`, which only the rule knows.
        var pending = groups.Single(g => g.Section == AlertSection.Pending);
        Assert.Contains("for: 10m", pending.Why, StringComparison.Ordinal);

        // Silenced says who and until when — the half worth anything three weeks later.
        var silenced = groups.Single(g => g.Section == AlertSection.Silenced);
        Assert.Contains("rick", silenced.Why, StringComparison.Ordinal);
        Assert.Contains("fix in #412", silenced.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_receiver_is_only_claimed_when_every_instance_agrees()
    {
        var one = Alert("A") with { Receivers = ["pagerduty"] };
        var same = Alert("A", pod: "p-2") with { Receivers = ["pagerduty"] };
        var other = Alert("A", pod: "p-3") with { Receivers = ["slack-infra"] };

        Assert.Equal("pagerduty", ClusterAlertsViewModel.Group([one, same], [], [])[0].Receiver);

        // Alertmanager can match more than one route; naming one of them would be a guess about
        // where the page actually went.
        Assert.Null(ClusterAlertsViewModel.Group([one, other], [], [])[0].Receiver);
    }

    [Fact]
    public void The_badge_counts_things_to_look_at_not_things()
    {
        var alerts = new[]
        {
            Alert("A"),
            Alert("B"),
            Alert("C", state: AlertState.Pending),
            Alert("D", silencedBy: ["sil-1"]),
        };

        // Pending may never fire and silenced was already decided about; counting either turns the
        // badge into a number people learn to ignore.
        Assert.Equal(2, ClusterAlertsViewModel.BadgeCount(alerts));
        Assert.Equal(0, ClusterAlertsViewModel.BadgeCount([]));
    }

    [Fact]
    public async Task The_badge_number_matches_what_the_seeded_page_shows()
    {
        var page = await PageAsync();
        var alerts = await new FakeAlertSource().ListAlertsAsync();

        // Both come off the same data, so a change to one that does not move the other is a bug.
        Assert.Equal(
            ClusterAlertsViewModel.BadgeCount(alerts).ToString(System.Globalization.CultureInfo.InvariantCulture),
            page.FiringCaption);
    }

    [Fact]
    public async Task Searching_for_a_pod_finds_the_group_it_is_in()
    {
        var page = await PageAsync();

        // The instance is what a person remembers; the alertname is only what the list is keyed by.
        page.SearchText = "redis-7d9c4f";
        Assert.Single(page.Firing);
        Assert.Equal("KubePodCrashLooping", page.Firing[0].Name);

        page.SearchText = "nothing-like-this";
        Assert.False(page.HasFiring);
        Assert.True(page.HasNoMatches);
    }

    [Fact]
    public async Task Without_an_Alertmanager_the_page_explains_itself_instead_of_showing_an_empty_list()
    {
        var page = await PageAsync(new FakeClusterEngine { HasAlertmanager = false });

        Assert.False(page.HasAlerting);
        Assert.False(page.HasFiring);

        // Not the all-clear: "nothing is wrong" and "nothing answered" are different sentences and
        // the page must not use the friendlier one for the worse case.
        Assert.False(page.IsAllClear);

        // The CRD half is independent, and the notice says which half is missing.
        Assert.True(page.CanApplyRules);
        Assert.Contains("can be applied", page.RulesNotice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_two_capabilities_degrade_independently_in_what_the_page_says()
    {
        var noCrd = await PageAsync(new FakeClusterEngine { HasPrometheusRuleCrd = false });

        Assert.True(noCrd.HasAlerting);
        Assert.False(noCrd.CanApplyRules);
        Assert.Contains("not installed", noCrd.RulesNotice, StringComparison.Ordinal);
        Assert.Contains("exported to a file", noCrd.RulesNotice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_list_that_cannot_follow_the_cluster_says_so()
    {
        var page = await PageAsync();

        // Alertmanager has no watch stream. A page that silently never moves looks exactly like a
        // cluster where nothing is happening (KON-250).
        Assert.NotNull(page.LiveNotice);
        Assert.False(page.IsLive);
    }

    [Fact]
    public void Colour_is_never_the_only_signal()
    {
        var groups = ClusterAlertsViewModel.Group(
            [
                Alert("A", severity: "critical"),
                Alert("B", severity: "warning"),
                Alert("C", state: AlertState.Pending),
                Alert("D", silencedBy: ["s"]),
            ],
            [], []);

        // Every group that has a colour also carries a word saying the same thing.
        Assert.All(groups, g => Assert.NotEmpty(g.SectionWord));

        Assert.Equal("Danger", groups.Single(g => g.Name == "A").SeverityBrushKey);
        Assert.Equal("Warn", groups.Single(g => g.Name == "B").SeverityBrushKey);
        Assert.Equal("Info", groups.Single(g => g.Name == "C").SeverityBrushKey);
        Assert.Equal("pending", groups.Single(g => g.Name == "C").SectionWord);
        Assert.Equal("silenced", groups.Single(g => g.Name == "D").SectionWord);
    }

    [Fact]
    public void An_instance_names_the_most_specific_object_it_carries()
    {
        var node = new Alert
        {
            Labels = new Dictionary<string, string>
            {
                ["alertname"] = "KubeMemoryOvercommit",
                ["severity"] = "warning",
                ["node"] = "gke-pool-b-2",
            },
            StartsAt = DateTimeOffset.UtcNow.AddHours(-2),
        };

        var instance = ClusterAlertsViewModel.Group([node], [], [])[0].Instances[0];

        Assert.Equal("gke-pool-b-2", instance.Target);

        // The header already says the alertname and the severity, so the detail line does not repeat
        // them — nor the label it just used as the target.
        Assert.DoesNotContain("alertname", instance.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("severity", instance.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("gke-pool-b-2", instance.Detail, StringComparison.Ordinal);
    }
}
