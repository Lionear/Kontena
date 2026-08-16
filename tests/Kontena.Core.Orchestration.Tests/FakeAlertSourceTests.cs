using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;
using Xunit;

namespace Kontena.Core.Orchestration.Tests;

public class FakeAlertSourceTests
{
    [Fact]
    public async Task Seed_fills_every_section_of_the_list()
    {
        var alerts = await new FakeAlertSource().ListAlertsAsync();

        Assert.Equal(2, alerts.Count(a => a.Name == "KubePodCrashLooping"));
        Assert.Equal(4, alerts.Count(a => a.State == AlertState.Firing && !a.IsSilenced));
        Assert.Single(alerts, a => a.State == AlertState.Pending);
        Assert.Single(alerts, a => a.IsSilenced);
    }

    [Fact]
    public async Task Severity_is_read_off_the_labels()
    {
        var alert = (await new FakeAlertSource().ListAlertsAsync()).First(a => a.Name == "CertificateExpiringSoon");

        Assert.Equal("warning", alert.Severity);
        Assert.Equal("api-tls", alert.Labels["secret"]);
        Assert.Null(alert.Labels.GetValueOrDefault("nosuchlabel"));
    }

    [Fact]
    public async Task Creating_a_silence_mutes_the_alerts_it_matches_and_expiring_it_unmutes_them()
    {
        var source = new FakeAlertSource();
        var now = DateTimeOffset.UtcNow;

        var id = await source.CreateSilenceAsync(new SilenceRequest
        {
            Matchers = [new SilenceMatcher { Name = "alertname", Value = "KubePodCrashLooping" }],
            StartsAt = now,
            EndsAt = now.AddHours(2),
            CreatedBy = "rick",
            Comment = "restarting the cache",
        });

        var muted = await source.ListAlertsAsync();
        Assert.Equal(2, muted.Count(a => a.SilencedBy.Contains(id)));
        // The rest of the list is untouched — a silence mutes what it matches and nothing else.
        Assert.Single(muted, a => a.Name == "KubeJobFailed" && a.IsSilenced);

        await source.ExpireSilenceAsync(id);

        var unmuted = await source.ListAlertsAsync();
        Assert.DoesNotContain(unmuted, a => a.SilencedBy.Contains(id));
        Assert.Single(unmuted, a => a.IsSilenced);
        Assert.Equal(SilenceStatus.Expired, (await source.ListSilencesAsync()).Single(s => s.Id == id).Status);
    }

    [Fact]
    public async Task A_regex_matcher_is_anchored_the_way_Alertmanager_anchors_it()
    {
        var source = new FakeAlertSource();
        var now = DateTimeOffset.UtcNow;

        // "redis" unanchored would also match "redis-7d9c4f-x2ktp"; anchored, it matches nothing.
        var id = await source.CreateSilenceAsync(new SilenceRequest
        {
            Matchers = [new SilenceMatcher { Name = "pod", Value = "redis", IsRegex = true }],
            StartsAt = now,
            EndsAt = now.AddHours(1),
        });

        Assert.DoesNotContain(await source.ListAlertsAsync(), a => a.SilencedBy.Contains(id));

        var wide = await source.CreateSilenceAsync(new SilenceRequest
        {
            Matchers = [new SilenceMatcher { Name = "pod", Value = "redis-.*", IsRegex = true }],
            StartsAt = now,
            EndsAt = now.AddHours(1),
        });

        Assert.Single(await source.ListAlertsAsync(), a => a.SilencedBy.Contains(wide));
    }

    [Fact]
    public async Task An_expression_that_parses_and_matches_nothing_is_not_an_error()
    {
        var source = new FakeAlertSource();

        var broken = await source.CheckExprAsync("sum(rate(foo{job=\"x\"[5m]))");
        Assert.False(broken.Parsed);
        Assert.NotNull(broken.Error);

        var typo = await source.CheckExprAsync("up{jobb=\"checkout\"}");
        Assert.True(typo.Parsed);
        Assert.True(typo.MatchesNothing);
        Assert.Null(typo.Error);

        var good = await source.CheckExprAsync("sum(rate(http_requests_total{job=\"checkout\"}[5m]))");
        Assert.False(good.MatchesNothing);
        Assert.Equal(2, good.Samples.Count);
    }

    [Fact]
    public void Capability_flags_degrade_independently()
    {
        Assert.True(new FakeClusterEngine().Capabilities.Alerting);
        Assert.IsType<FakeAlertSource>(new FakeClusterEngine().Alerts);

        var noAlertmanager = new FakeClusterEngine { HasAlertmanager = false };
        Assert.False(noAlertmanager.Capabilities.Alerting);
        Assert.True(noAlertmanager.Capabilities.AlertRules);
        Assert.Same(NoAlertSource.Instance, noAlertmanager.Alerts);

        var noCrd = new FakeClusterEngine { HasPrometheusRuleCrd = false };
        Assert.True(noCrd.Capabilities.Alerting);
        Assert.False(noCrd.Capabilities.AlertRules);
    }

    [Fact]
    public async Task The_null_source_reads_empty_and_refuses_to_pretend_it_silenced_anything()
    {
        Assert.Empty(await NoAlertSource.Instance.ListAlertsAsync());
        Assert.Empty(await NoAlertSource.Instance.ListRulesAsync());
        Assert.Empty(await NoAlertSource.Instance.ListSilencesAsync());

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await NoAlertSource.Instance.CreateSilenceAsync(new SilenceRequest { Matchers = [] }));
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await NoAlertSource.Instance.ExpireSilenceAsync("sil-0001"));
    }

    [Fact]
    public async Task Rules_expose_the_state_the_alert_list_cannot()
    {
        var rules = await new FakeAlertSource().ListRulesAsync();

        // A rule that is loaded, evaluating, and false — indistinguishable from a rule that was
        // never picked up if you only look at the alerts.
        var inactive = rules.Single(r => r.State == AlertState.Inactive);
        Assert.Equal("AppHighErrorRate", inactive.Name);
        Assert.Equal("ok", inactive.Health);
        Assert.Equal(TimeSpan.FromMinutes(10), inactive.For);
        Assert.Equal("critical", inactive.Severity);
    }
}
