using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Core.Orchestration.Fakes;

/// <summary>
/// An in-memory <see cref="IAlertSource"/> seeded to match the alerting mockup, so the alerts page,
/// the detail drawer and the rule editor can be built before <c>Kontena.Adapters.Kubernetes</c> can
/// reach a real Alertmanager — the same role <see cref="FakeClusterEngine"/> plays for the rest of
/// the OAL. No cluster, no network.
/// <para>
/// The seed is the mockup's, deliberately: two <c>KubePodCrashLooping</c> instances under one
/// alertname (so grouping has something to group), one node-level and one certificate warning, one
/// pending rule still waiting out its <c>for</c>, and one silenced job. That is every section of the
/// list with at least one row in it.
/// </para>
/// <para>
/// Silences are live rather than static: creating one mutes the alerts it matches and expiring it
/// unmutes them, so the drawer's silence flow can be driven end to end without a cluster.
/// </para>
/// </summary>
public sealed class FakeAlertSource : IAlertSource
{
    private readonly List<Alert> _alerts;
    private readonly List<AlertRule> _rules;
    private readonly List<Silence> _silences;

    private int _nextSilence = 2;

    public FakeAlertSource()
    {
        var now = DateTimeOffset.UtcNow;

        // The one silence in the seed: someone knows the migrate job fails and said so.
        _silences =
        [
            new Silence
            {
                Id = "sil-0001",
                Matchers =
                [
                    new SilenceMatcher { Name = "alertname", Value = "KubeJobFailed" },
                    new SilenceMatcher { Name = "namespace", Value = "app" },
                    new SilenceMatcher { Name = "job", Value = "migrate" },
                ],
                StartsAt = now.AddHours(-3),
                EndsAt = now.AddHours(2),
                CreatedBy = "rick",
                Comment = "migrate job, known, fix in #412",
            },
        ];

        _alerts =
        [
            new Alert
            {
                Fingerprint = "a1b2c3d4e5f60001",
                Labels = new Dictionary<string, string>
                {
                    ["alertname"] = "KubePodCrashLooping",
                    ["severity"] = "critical",
                    ["namespace"] = "app",
                    ["pod"] = "redis-7d9c4f-x2ktp",
                    ["container"] = "redis",
                },
                Annotations = new Dictionary<string, string>
                {
                    ["summary"] = "Pod app/redis-7d9c4f-x2ktp has restarted 84 times in the last hour.",
                    ["runbook_url"] = "https://wiki/runbooks/pod-crashloop",
                },
                StartsAt = now.AddHours(-6).AddMinutes(-12),
                Receivers = ["pagerduty"],
            },
            new Alert
            {
                Fingerprint = "a1b2c3d4e5f60002",
                Labels = new Dictionary<string, string>
                {
                    ["alertname"] = "KubePodCrashLooping",
                    ["severity"] = "critical",
                    ["namespace"] = "app",
                    ["pod"] = "worker-5f8b9d-qq4mn",
                    ["container"] = "worker",
                },
                Annotations = new Dictionary<string, string>
                {
                    ["summary"] = "Pod app/worker-5f8b9d-qq4mn has restarted 11 times in the last hour.",
                    ["runbook_url"] = "https://wiki/runbooks/pod-crashloop",
                },
                StartsAt = now.AddMinutes(-41),
                Receivers = ["pagerduty"],
            },
            new Alert
            {
                Fingerprint = "a1b2c3d4e5f60003",
                Labels = new Dictionary<string, string>
                {
                    ["alertname"] = "KubeMemoryOvercommit",
                    ["severity"] = "warning",
                    ["node"] = "gke-pool-b-2",
                },
                Annotations = new Dictionary<string, string>
                {
                    ["summary"] = "Node gke-pool-b-2 has more memory requested than it can schedule.",
                },
                StartsAt = now.AddHours(-2).AddMinutes(-4),
                Receivers = ["slack-infra"],
            },
            new Alert
            {
                Fingerprint = "a1b2c3d4e5f60004",
                Labels = new Dictionary<string, string>
                {
                    ["alertname"] = "CertificateExpiringSoon",
                    ["severity"] = "warning",
                    ["namespace"] = "ingress",
                    ["secret"] = "api-tls",
                    ["host"] = "api.example.com",
                },
                Annotations = new Dictionary<string, string>
                {
                    ["summary"] = "The certificate for api.example.com expires in 11 days.",
                },
                StartsAt = now.AddHours(-9).AddMinutes(-30),
                Receivers = ["slack-infra"],
            },
            // Pending: Prometheus has it, Alertmanager has not been told — hence no receiver.
            new Alert
            {
                Fingerprint = "a1b2c3d4e5f60005",
                Labels = new Dictionary<string, string>
                {
                    ["alertname"] = "HighRequestLatency",
                    ["severity"] = "warning",
                    ["namespace"] = "app",
                    ["service"] = "checkout",
                },
                Annotations = new Dictionary<string, string>
                {
                    ["summary"] = "Checkout p99 latency is above 1s.",
                },
                State = AlertState.Pending,
                StartsAt = now.AddMinutes(-3),
            },
            new Alert
            {
                Fingerprint = "a1b2c3d4e5f60006",
                Labels = new Dictionary<string, string>
                {
                    ["alertname"] = "KubeJobFailed",
                    ["severity"] = "warning",
                    ["namespace"] = "app",
                    ["job"] = "migrate",
                },
                Annotations = new Dictionary<string, string>
                {
                    ["summary"] = "Job app/migrate failed to complete.",
                },
                StartsAt = now.AddHours(-3).AddMinutes(-40),
                Receivers = ["slack-infra"],
                SilencedBy = ["sil-0001"],
            },
        ];

        _rules =
        [
            Rule("KubePodCrashLooping", "kubernetes-apps", "critical", TimeSpan.FromMinutes(15), AlertState.Firing,
                "increase(kube_pod_container_status_restarts_total{job=\"kube-state-metrics\"}[10m]) > 3"),
            Rule("KubeMemoryOvercommit", "kubernetes-resources", "warning", TimeSpan.FromMinutes(5), AlertState.Firing,
                "sum(kube_pod_container_resource_requests{resource=\"memory\"}) by (node) > sum(kube_node_status_allocatable{resource=\"memory\"}) by (node)"),
            Rule("CertificateExpiringSoon", "certificates", "warning", TimeSpan.FromHours(1), AlertState.Firing,
                "certmanager_certificate_expiration_timestamp_seconds - time() < 86400 * 14"),
            Rule("HighRequestLatency", "app-slo", "warning", TimeSpan.FromMinutes(10), AlertState.Pending,
                "histogram_quantile(0.99, sum(rate(http_request_duration_seconds_bucket{job=\"checkout\"}[5m])) by (le)) > 1"),
            Rule("KubeJobFailed", "kubernetes-apps", "warning", TimeSpan.FromMinutes(15), AlertState.Firing,
                "kube_job_failed{job=\"kube-state-metrics\"} > 0"),
            // Loaded, evaluating, false — the state a freshly applied rule sits in, which the
            // "applied but not firing" result screen has to be able to show.
            Rule("AppHighErrorRate", "checkout-slo", "critical", TimeSpan.FromMinutes(10), AlertState.Inactive,
                "sum(rate(http_requests_total{job=\"checkout\",status=~\"5..\"}[5m]))\n  / sum(rate(http_requests_total{job=\"checkout\"}[5m])) > 0.05"),
        ];
    }

    private static AlertRule Rule(string name, string group, string severity, TimeSpan @for, AlertState state, string expr) =>
        new()
        {
            Name = name,
            Expr = expr,
            Group = group,
            Namespace = "monitoring",
            For = @for,
            Labels = new Dictionary<string, string> { ["severity"] = severity },
            State = state,
            Health = "ok",
        };

    public string Name => "alertmanager";

    public ValueTask<IReadOnlyList<Alert>> ListAlertsAsync(CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<Alert>>([.. _alerts]);

    public ValueTask<IReadOnlyList<AlertRule>> ListRulesAsync(CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<AlertRule>>([.. _rules]);

    public ValueTask<IReadOnlyList<Silence>> ListSilencesAsync(CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<Silence>>([.. _silences]);

    public ValueTask<string> CreateSilenceAsync(SilenceRequest request, CancellationToken ct = default)
    {
        var id = $"sil-{_nextSilence++:0000}";
        _silences.Add(new Silence
        {
            Id = id,
            Matchers = request.Matchers,
            StartsAt = request.StartsAt,
            EndsAt = request.EndsAt,
            CreatedBy = request.CreatedBy,
            Comment = request.Comment,
        });

        Restamp(id, request.Matchers, silenced: true);
        return ValueTask.FromResult(id);
    }

    public ValueTask ExpireSilenceAsync(string id, CancellationToken ct = default)
    {
        var i = _silences.FindIndex(s => s.Id == id);
        if (i < 0)
            throw new InvalidOperationException($"No silence with id '{id}'.");

        var silence = _silences[i];
        _silences[i] = silence with { EndsAt = DateTimeOffset.UtcNow, Status = SilenceStatus.Expired };

        Restamp(id, silence.Matchers, silenced: false);
        return ValueTask.CompletedTask;
    }

    /// <summary>Add or remove <paramref name="id"/> on every alert the matchers cover.</summary>
    private void Restamp(string id, IReadOnlyList<SilenceMatcher> matchers, bool silenced)
    {
        for (var i = 0; i < _alerts.Count; i++)
        {
            var alert = _alerts[i];
            if (!matchers.All(m => m.Matches(alert.Labels.GetValueOrDefault(m.Name, string.Empty))))
                continue;

            _alerts[i] = alert with
            {
                SilencedBy = silenced
                    ? [.. alert.SilencedBy.Append(id).Distinct()]
                    : [.. alert.SilencedBy.Where(s => s != id)],
            };
        }
    }

    /// <summary>
    /// Answers the way Prometheus would: unbalanced braces are a parse error, and anything the fake
    /// has no series for evaluates cleanly to nothing. That second case is the point — a mistyped
    /// label name is always valid PromQL, and an empty result is its only symptom.
    /// </summary>
    public ValueTask<ExprCheck> CheckExprAsync(string promql, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(promql))
            return Fail("parse error: no expression found in input");

        if (promql.Count(c => c == '{') != promql.Count(c => c == '}'))
            return Fail("parse error: unexpected end of input inside braces");

        IReadOnlyList<ExprSample> samples = promql.Contains("http_requests_total", StringComparison.Ordinal)
            ?
            [
                new ExprSample(new Dictionary<string, string> { ["job"] = "checkout", ["pod"] = "checkout-6b4-d92wq" }, 0.071),
                new ExprSample(new Dictionary<string, string> { ["job"] = "checkout", ["pod"] = "checkout-6b4-tl8vf" }, 0.004),
            ]
            : [];

        return ValueTask.FromResult(new ExprCheck { Parsed = true, Samples = samples });

        static ValueTask<ExprCheck> Fail(string error) =>
            ValueTask.FromResult(new ExprCheck { Parsed = false, Error = error });
    }
}
