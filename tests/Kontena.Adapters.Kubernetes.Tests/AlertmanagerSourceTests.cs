using System.Net;
using Kontena.Adapters.Kubernetes;
using Kontena.Sdk.Orchestration.Models;
using Xunit;

namespace Kontena.Adapters.Kubernetes.Tests;

public class AlertmanagerSourceTests
{
    private static readonly ServiceEndpoint Alertmanager = new("monitoring", "alertmanager-operated", 9093);
    private static readonly ServiceEndpoint Prometheus = new("monitoring", "prometheus-operated", 9090);

    /// <summary>One request as it went out — read eagerly, because the message is disposed after.</summary>
    private sealed record Sent(HttpMethod Method, Uri Uri, string? Body);

    /// <summary>Answers canned JSON per path fragment, and remembers what was asked for.</summary>
    private sealed class Proxy(params (string Contains, string Body)[] routes) : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = [];
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(new Sent(
                request.Method,
                request.RequestUri!,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));

            var path = request.RequestUri!.PathAndQuery;
            var body = routes.FirstOrDefault(r => path.Contains(r.Contains, StringComparison.Ordinal)).Body;

            return new HttpResponseMessage(body is null ? HttpStatusCode.NotFound : Status)
            {
                Content = new StringContent(body ?? "{}"),
            };
        }
    }

    private static AlertmanagerSource Source(
        Proxy handler, ServiceEndpoint? alertmanager = null, ServiceEndpoint? prometheus = null) =>
        new(new ApiProxyHttp(new HttpClient(handler), new Uri("https://10.0.0.1:6443/")),
            alertmanager ?? Alertmanager,
            prometheus ?? Prometheus);

    private const string OneAlert = """
    [{
      "labels": {"alertname":"KubePodCrashLooping","severity":"critical","namespace":"app","pod":"redis-7d9c4f-x2ktp"},
      "annotations": {"summary":"Pod app/redis-7d9c4f-x2ktp has restarted 84 times in the last hour."},
      "startsAt": "2026-07-30T06:41:03.000Z",
      "endsAt": "2026-07-30T18:41:03.000Z",
      "fingerprint": "a1b2c3d4e5f60001",
      "receivers": [{"name":"pagerduty"}],
      "status": {"state":"suppressed","silencedBy":["sil-0001"],"inhibitedBy":[]}
    }]
    """;

    private const string OneRule = """
    {"status":"success","data":{"groups":[{"name":"kubernetes-apps","file":"/etc/prometheus/rules/x.yaml","rules":[
      {"type":"recording","name":"job:up:sum","query":"sum(up)"},
      {"type":"alerting","name":"HighRequestLatency","query":"histogram_quantile(0.99, x) > 1",
       "duration":600,"labels":{"severity":"warning"},"annotations":{"summary":"slow"},
       "state":"pending","health":"ok","lastError":"",
       "alerts":[{"labels":{"alertname":"HighRequestLatency","namespace":"app"},"annotations":{},
                  "state":"pending","activeAt":"2026-07-30T12:00:00.000Z"}]}
    ]}]}}
    """;

    [Fact]
    public async Task An_alert_keeps_its_labels_and_reports_who_silenced_it()
    {
        var alerts = await Source(new Proxy(("api/v2/alerts", OneAlert))).ListAlertsAsync();

        var alert = Assert.Single(alerts);
        Assert.Equal("KubePodCrashLooping", alert.Name);
        Assert.Equal("critical", alert.Severity);
        Assert.Equal("redis-7d9c4f-x2ktp", alert.Labels["pod"]);
        Assert.Equal("a1b2c3d4e5f60001", alert.Fingerprint);
        Assert.Equal(["pagerduty"], alert.Receivers);

        // Suppressed in Alertmanager's words is still firing underneath — the state does not change,
        // only SilencedBy does.
        Assert.Equal(AlertState.Firing, alert.State);
        Assert.True(alert.IsSilenced);
        Assert.Equal(["sil-0001"], alert.SilencedBy);

        Assert.Equal(DateTimeOffset.Parse("2026-07-30T06:41:03Z", System.Globalization.CultureInfo.InvariantCulture),
            alert.StartsAt);
    }

    [Fact]
    public async Task Silenced_and_inhibited_alerts_are_asked_for_rather_than_filtered_out()
    {
        var handler = new Proxy(("api/v2/alerts", OneAlert));
        await Source(handler).ListAlertsAsync();

        // Alertmanager hides both by default. The list has a Silenced section, so it needs them.
        var query = Assert.Single(handler.Requests, r => r.Uri.AbsolutePath.EndsWith("/api/v2/alerts", StringComparison.Ordinal)).Uri.Query;
        Assert.Contains("silenced=true", query, StringComparison.Ordinal);
        Assert.Contains("inhibited=true", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recording_rules_are_not_alert_rules()
    {
        var rules = await Source(new Proxy(("api/v1/rules", OneRule))).ListRulesAsync();

        var rule = Assert.Single(rules);
        Assert.Equal("HighRequestLatency", rule.Name);
        Assert.Equal("kubernetes-apps", rule.Group);
        Assert.Equal(TimeSpan.FromMinutes(10), rule.For);
        Assert.Equal(AlertState.Pending, rule.State);
        Assert.Equal("ok", rule.Health);
        Assert.Null(rule.LastError);

        // Prometheus reports a rule file, not the PrometheusRule it came from. Guessing the
        // namespace out of the filename would put a wrong jump link on the drawer.
        Assert.Null(rule.Namespace);
    }

    [Fact]
    public async Task Pending_instances_come_from_Prometheus_because_Alertmanager_never_hears_about_them()
    {
        var alerts = await Source(new Proxy(("api/v2/alerts", "[]"), ("api/v1/rules", OneRule))).ListAlertsAsync();

        var pending = Assert.Single(alerts);
        Assert.Equal(AlertState.Pending, pending.State);
        Assert.Equal("HighRequestLatency", pending.Name);
        // Nothing routed it anywhere yet, and saying otherwise would invent a receiver.
        Assert.Empty(pending.Receivers);
    }

    [Fact]
    public async Task Each_half_keeps_working_when_the_other_is_missing()
    {
        var noPrometheus = Source(new Proxy(("api/v2/alerts", OneAlert)), prometheus: null);
        Assert.Single(await noPrometheus.ListAlertsAsync());
        Assert.Empty(await noPrometheus.ListRulesAsync());
        Assert.False((await noPrometheus.CheckExprAsync("up")).Parsed);

        var noAlertmanager = new AlertmanagerSource(
            new ApiProxyHttp(new HttpClient(new Proxy(("api/v1/rules", OneRule))), new Uri("https://10.0.0.1:6443/")),
            alertmanager: null,
            Prometheus);

        Assert.Single(await noAlertmanager.ListRulesAsync());
        Assert.Empty(await noAlertmanager.ListSilencesAsync());
        Assert.Equal("none", noAlertmanager.Name);
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await noAlertmanager.CreateSilenceAsync(new SilenceRequest { Matchers = [] }));
    }

    [Fact]
    public async Task A_silence_round_trips_through_Alertmanagers_own_spelling()
    {
        var handler = new Proxy(("api/v2/silences", """{"silenceID":"sil-0007"}"""));
        var source = Source(handler);

        var id = await source.CreateSilenceAsync(new SilenceRequest
        {
            Matchers = [new SilenceMatcher { Name = "alertname", Value = "KubeJobFailed" }],
            StartsAt = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero),
            EndsAt = new DateTimeOffset(2026, 7, 30, 14, 0, 0, TimeSpan.Zero),
            CreatedBy = "rick",
            Comment = "known",
        });

        Assert.Equal("sil-0007", id);

        var body = handler.Requests[0].Body!;
        Assert.Contains("\"isRegex\":false", body, StringComparison.Ordinal);
        Assert.Contains("\"isEqual\":true", body, StringComparison.Ordinal);
        Assert.Contains("2026-07-30T14:00:00", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Expiring_uses_the_singular_path_and_a_refusal_is_raised_not_swallowed()
    {
        var handler = new Proxy(("api/v2/silence/", "{}"));
        await Source(handler).ExpireSilenceAsync("sil-0007");

        // Alertmanager creates against /silences and expires against /silence/{id}.
        Assert.EndsWith("/api/v2/silence/sil-0007", handler.Requests[0].Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Delete, handler.Requests[0].Method);

        // A silence that failed must not look like one that worked.
        var refused = Source(new Proxy(("api/v2/silences", "{}")) { Status = HttpStatusCode.Forbidden });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await refused.CreateSilenceAsync(new SilenceRequest { Matchers = [] }));
        Assert.Contains("services/proxy", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_silence_from_before_isEqual_existed_still_reads_as_equality()
    {
        const string Silences = """
        [{"id":"sil-0001","createdBy":"rick","comment":"known",
          "startsAt":"2026-07-30T09:00:00.000Z","endsAt":"2026-07-30T18:00:00.000Z",
          "status":{"state":"active"},
          "matchers":[{"name":"alertname","value":"KubeJobFailed","isRegex":false},
                      {"name":"pod","value":"redis-.*","isRegex":true,"isEqual":false}]}]
        """;

        var silence = Assert.Single(await Source(new Proxy(("api/v2/silences", Silences))).ListSilencesAsync());

        Assert.Equal("rick", silence.CreatedBy);
        Assert.Equal(SilenceStatus.Active, silence.Status);

        // Alertmanager only started sending isEqual in 0.22; absent has to mean "=", not "!=".
        Assert.True(silence.Matchers[0].IsEqual);
        Assert.True(silence.Matchers[0].Matches("KubeJobFailed"));

        Assert.False(silence.Matchers[1].IsEqual);
        Assert.False(silence.Matchers[1].Matches("redis-7d9c4f"));
        Assert.True(silence.Matchers[1].Matches("worker-5f8b9d"));
    }

    [Fact]
    public async Task A_rejected_expression_comes_back_in_Prometheus_own_words()
    {
        var rejected = Source(new Proxy(("api/v1/query", """{"status":"error","errorType":"bad_data","error":"parse error at char 9: unexpected end of input"}"""))
            { Status = HttpStatusCode.BadRequest });

        var check = await rejected.CheckExprAsync("sum(rate(");
        Assert.False(check.Parsed);
        Assert.Equal("parse error at char 9: unexpected end of input", check.Error);

        // Valid and matching nothing is the case the check exists for, and it is not an error.
        var empty = Source(new Proxy(("api/v1/query", """{"status":"success","data":{"resultType":"vector","result":[]}}""")));
        var none = await empty.CheckExprAsync("up{jobb=\"checkout\"}");
        Assert.True(none.Parsed);
        Assert.True(none.MatchesNothing);
        Assert.Null(none.Error);
    }

    [Fact]
    public async Task Samples_keep_their_labels_and_their_value()
    {
        const string Vector = """
        {"status":"success","data":{"resultType":"vector","result":[
          {"metric":{"job":"checkout","pod":"checkout-6b4-d92wq"},"value":[1785000000,"0.071"]},
          {"metric":{"job":"checkout","pod":"checkout-6b4-tl8vf"},"value":[1785000000,"NaN"]}
        ]}}
        """;

        var check = await Source(new Proxy(("api/v1/query", Vector))).CheckExprAsync("rate(http_requests_total[5m])");

        Assert.True(check.Parsed);
        Assert.False(check.MatchesNothing);
        Assert.Equal(2, check.Samples.Count);
        Assert.Equal("checkout-6b4-d92wq", check.Samples[0].Labels["pod"]);
        Assert.Equal(0.071, check.Samples[0].Value, 3);
        Assert.True(double.IsNaN(check.Samples[1].Value));
    }

    [Fact]
    public async Task Garbage_on_the_wire_is_an_empty_list_and_not_a_crash()
    {
        // A proxy that returns an error page, a truncated body, or an unexpected shape — a list page
        // has to survive all three, because none of them is worth taking the cluster down over.
        var source = Source(new Proxy(("api/v2/alerts", "<html>503</html>"), ("api/v1/rules", """{"status":"success"}""")));

        Assert.Empty(await source.ListAlertsAsync());
        Assert.Empty(await source.ListRulesAsync());
        Assert.Empty(await source.ListSilencesAsync());
    }
}
