using System.Globalization;
using System.Text.Json;
using k8s;
using k8s.Models;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Adapters.Kubernetes;

/// <summary>
/// Pod usage history from a Prometheus already running in the cluster (KON-345).
/// <para>
/// Reached through the apiserver's service proxy — <c>/api/v1/namespaces/{ns}/services/{svc}:{port}/proxy/…</c>
/// — the same trick <see cref="KubeletSummarySource"/> uses for the kubelet. That means no
/// port-forward to manage, no second set of credentials, and it works from wherever kubectl works.
/// The cost is that it needs <c>services/proxy</c> RBAC, which some managed clusters withhold; a
/// refusal is reported as "no history" rather than as an error.
/// </para>
/// <para>
/// Deliberately read-only and deliberately narrow: two queries against cAdvisor series that any
/// kube-prometheus-stack or prometheus-community install scrapes by default. It is not a general
/// PromQL client and does not try to be one.
/// </para>
/// </summary>
internal sealed class PrometheusSource(HttpClient http, Uri apiServer, IKubernetes client) : IMetricsHistory
{
    /// <summary>Points to aim for in a range query — enough to see shape, few enough to draw fast.</summary>
    private const int TargetPoints = 120;

    private ServiceEndpoint? _endpoint;

    public string Name => "Prometheus";

    public bool IsAvailable => _endpoint is not null;

    /// <summary>Where the Prometheus that answered lives, for the UI to name.</summary>
    public string? Location => _endpoint is null ? null : $"{_endpoint.Namespace}/{_endpoint.Service}";

    public async ValueTask<bool> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            foreach (var candidate in await DiscoverAsync(ct).ConfigureAwait(false))
            {
                if (await AnswersAsync(candidate, ct).ConfigureAwait(false))
                {
                    _endpoint = candidate;
                    return true;
                }
            }
        }
        catch (Exception)
        {
            // No services/proxy permission, no Prometheus, an apiserver that said no — all of them
            // mean the same thing to the UI, and none of them is worth an error on a pod page.
        }

        _endpoint = null;
        return false;
    }

    /// <summary>Every scope. The node one comes from node-exporter — see <see cref="NodeSeries"/>.</summary>
    public bool Supports(UsageScope scope) => true;

    public async ValueTask<IReadOnlyList<UsageSample>> GetHistoryAsync(
        UsageTarget target, UsageMetric metric, TimeSpan range, CancellationToken ct = default)
    {
        if (_endpoint is not { } endpoint || !Supports(target.Scope))
            return [];

        var end = DateTimeOffset.UtcNow;
        var start = end - range;
        var step = StepFor(range);

        if (QueryFor(target, metric, RateWindow(step)) is not { } query)
            return [];
        var path = "api/v1/query_range"
                   + $"?query={Uri.EscapeDataString(query)}"
                   + $"&start={Unix(start)}&end={Unix(end)}"
                   + $"&step={(int)step.TotalSeconds}";

        var json = await GetAsync(endpoint, path, ct).ConfigureAwait(false);
        return json is null ? [] : ParseMatrix(json.Value);
    }

    public TimeSpan RefreshInterval(TimeSpan range) =>
        TimeSpan.FromSeconds(Math.Clamp(range.TotalSeconds / TargetPoints, 30, 300));

    // ── Query building ───────────────────────────────────────────────────────

    /// <summary>
    /// The step a range is answered at. Bounded below by 15s: asking for finer than the scrape
    /// interval returns the same value repeatedly and just makes the series longer.
    /// </summary>
    internal static TimeSpan StepFor(TimeSpan range) =>
        TimeSpan.FromSeconds(Math.Max(15, Math.Round(range.TotalSeconds / TargetPoints)));

    /// <summary>
    /// The window a rate is taken over. Must span at least two scrapes or the rate is empty, so it
    /// is kept at four times the step and never under two minutes.
    /// </summary>
    internal static TimeSpan RateWindow(TimeSpan step) =>
        TimeSpan.FromSeconds(Math.Max(120, step.TotalSeconds * 4));

    /// <summary>
    /// The PromQL for a target's CPU or memory, summed over its containers, or null when the target
    /// cannot be expressed — an unsupported scope, or a name no Kubernetes object could carry.
    /// <para>
    /// <c>container!=""</c> drops the pod-level rollup series and <c>container!="POD"</c> drops the
    /// pause container; without both, every value is counted twice.
    /// </para>
    /// </summary>
    internal static string? QueryFor(UsageTarget target, UsageMetric metric, TimeSpan rateWindow)
    {
        // Object names are RFC 1123 and cannot contain a quote, but the query is still built from
        // strings that came off the wire — a name that somehow does is refused rather than
        // interpolated into PromQL.
        if (!IsSafeName(target.Name) || (target.Namespace is { } n && !IsSafeName(n)))
            return null;

        var cadvisor = "container!=\"\",container!=\"POD\"";
        var window = (int)rateWindow.TotalSeconds;

        string series = target.Scope switch
        {
            UsageScope.Pod =>
                Metric(metric, $"namespace=\"{target.Namespace}\",pod=\"{target.Name}\",{cadvisor}", window),

            UsageScope.Namespace =>
                Metric(metric, $"namespace=\"{target.Name}\",{cadvisor}", window),

            // Everything running, summed. Deliberately the same cAdvisor series the other scopes
            // use rather than node-exporter: a cluster total that did not add up to the sum of its
            // namespaces would be two answers to one question.
            UsageScope.Cluster => Metric(metric, cadvisor, window),

            UsageScope.Node => NodeSeries(metric, target.Name, window),

            // Pods are traced to their workload through kube-state-metrics rather than by matching
            // pod names: a Deployment called "api" and one called "api-worker" produce pod names
            // that a prefix match cannot tell apart, and the pods a rollout replaced still count.
            UsageScope.Workload when OwnerSelector(target) is { } owner =>
                Metric(metric, $"namespace=\"{target.Namespace}\",{cadvisor}", window)
                + $" * on(namespace,pod) group_left() kube_pod_owner{{{owner}}}",

            _ => string.Empty,
        };

        if (series.Length == 0)
            return null;

        return metric == UsageMetric.Cpu
            // Cores × 1000: Kontena counts milli-cores everywhere else.
            ? $"sum({series}) * 1000"
            : $"sum({series})";
    }

    /// <summary>
    /// A node's own usage, from node-exporter, joined to the node name through
    /// <c>node_uname_info</c> — whose <c>nodename</c> is the host's name and whose <c>instance</c>
    /// is what the exporter's own series carry.
    /// <para>
    /// CPU excludes idle, iowait and steal, which is what the standard utilisation rule does and
    /// what brings it near the kubelet's figure; counting everything but idle reads roughly double.
    /// Memory is total minus available. Neither matches the kubelet exactly — the two measure a
    /// node differently — and the panel says so rather than leaving the difference to be noticed.
    /// </para>
    /// </summary>
    internal static string NodeSeries(UsageMetric metric, string node, int window)
    {
        var join = $"* on(instance) group_left() node_uname_info{{nodename=\"{node}\"}}";

        return metric == UsageMetric.Cpu
            ? $"rate(node_cpu_seconds_total{{mode!~\"idle|iowait|steal\"}}[{window}s]) {join}"
            : $"(node_memory_MemTotal_bytes - node_memory_MemAvailable_bytes) {join}";
    }

    private static string Metric(UsageMetric metric, string selector, int window) =>
        metric == UsageMetric.Cpu
            ? $"rate(container_cpu_usage_seconds_total{{{selector}}}[{window}s])"
            : $"container_memory_working_set_bytes{{{selector}}}";

    /// <summary>
    /// How <c>kube_pod_owner</c> names the owner of this workload's pods. A Deployment does not own
    /// pods directly — its ReplicaSets do, and they are named <c>&lt;deployment&gt;-&lt;hash&gt;</c>
    /// — and a CronJob owns them through a Job named <c>&lt;cronjob&gt;-&lt;timestamp&gt;</c>.
    /// PromQL anchors its regexes, so the hash pattern cannot spill onto a longer name.
    /// </summary>
    internal static string? OwnerSelector(UsageTarget target)
    {
        var ns = $"namespace=\"{target.Namespace}\"";
        var name = target.Name.Replace(".", "\\.", StringComparison.Ordinal);

        return target.Kind switch
        {
            "Deployment" => $"{ns},owner_kind=\"ReplicaSet\",owner_name=~\"{name}-[a-z0-9]+\"",
            "CronJob" => $"{ns},owner_kind=\"Job\",owner_name=~\"{name}-[0-9]+\"",
            "StatefulSet" or "DaemonSet" or "Job" =>
                $"{ns},owner_kind=\"{target.Kind}\",owner_name=\"{target.Name}\"",
            _ => null,
        };
    }

    /// <summary>Object names are RFC 1123; anything else never came from a Kubernetes object.</summary>
    internal static bool IsSafeName(string value) =>
        value.Length is > 0 and <= 253
        && value.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c is '-' or '.');

    private static long Unix(DateTimeOffset when) => when.ToUnixTimeSeconds();

    // ── Response parsing ─────────────────────────────────────────────────────

    /// <summary>
    /// The <c>matrix</c> result of a range query, flattened to one series.
    /// <para>
    /// The query sums, so there is at most one series; a response carrying several is treated as an
    /// answer Kontena did not ask for and only the first is read, rather than silently adding
    /// unrelated series together.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<UsageSample> ParseMatrix(JsonElement root)
    {
        if (!root.TryGetProperty("status", out var status) || status.GetString() != "success")
            return [];

        if (!root.TryGetProperty("data", out var data)
            || !data.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Array
            || result.GetArrayLength() == 0)
            return [];

        var first = result[0];
        if (!first.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
            return [];

        var samples = new List<UsageSample>(values.GetArrayLength());
        foreach (var point in values.EnumerateArray())
        {
            // [ <unix seconds, number>, "<value as string>" ] — Prometheus sends the value as text
            // so that NaN and Inf survive JSON.
            if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() != 2)
                continue;

            if (!point[0].TryGetDouble(out var seconds))
                continue;

            if (!double.TryParse(point[1].GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                || double.IsNaN(value) || double.IsInfinity(value))
                continue;

            samples.Add(new UsageSample(DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)), value));
        }

        return samples;
    }

    // ── Discovery ────────────────────────────────────────────────────────────

    /// <summary>A service that might be a Prometheus, and the port to try it on.</summary>
    internal sealed record ServiceEndpoint(string Namespace, string Service, int Port);

    /// <summary>
    /// Label selectors that find a Prometheus service, most specific first.
    /// <para>
    /// The middle one is not redundant: a <c>kube-prometheus-stack</c> from before the chart adopted
    /// the <c>app.kubernetes.io</c> labels carries only <c>app=kube-prometheus-stack-prometheus</c>,
    /// and installs of that age are exactly the ones with years of retention worth charting. Found
    /// on a real 41.7.3 install where the first selector matched nothing and discovery was falling
    /// through to the name heuristic by luck.
    /// </para>
    /// </summary>
    internal static readonly IReadOnlyList<string> Selectors =
    [
        "app.kubernetes.io/name=prometheus",
        "app=kube-prometheus-stack-prometheus",
        "app=prometheus",
    ];

    /// <summary>
    /// Services that look like a Prometheus, best candidate first.
    /// <para>
    /// By label rather than by name: <c>kube-prometheus-stack</c> names its service after the Helm
    /// release, so the name is whatever the operator chose, while the label is fixed by the chart.
    /// The name check is the fallback for installs that predate those labels.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<ServiceEndpoint>> DiscoverAsync(CancellationToken ct)
    {
        foreach (var selector in Selectors)
        {
            var services = await client.CoreV1
                .ListServiceForAllNamespacesAsync(labelSelector: selector, cancellationToken: ct)
                .ConfigureAwait(false);

            if (Candidates(services.Items) is { Count: > 0 } found)
                return found;
        }

        var all = await client.CoreV1.ListServiceForAllNamespacesAsync(cancellationToken: ct).ConfigureAwait(false);
        return Candidates(all.Items.Where(s => NamedLikePrometheus(s.Metadata?.Name)).ToList());
    }

    private static bool NamedLikePrometheus(string? name) =>
        name is not null
        && (name is "prometheus" or "prometheus-operated" or "prometheus-server" or "prometheus-k8s"
            || name.EndsWith("-prometheus", StringComparison.Ordinal));

    /// <summary>
    /// Ranked candidates. A service with a cluster IP comes before a headless one: both proxy, but
    /// the headless <c>prometheus-operated</c> exists alongside a normal service on every
    /// kube-prometheus-stack, and the normal one is the supported way in.
    /// </summary>
    internal static IReadOnlyList<ServiceEndpoint> Candidates(IList<V1Service> services) =>
        [.. services
            .Where(s => s.Metadata?.Name is not null && s.Metadata.NamespaceProperty is not null)
            .Select(s => new
            {
                Service = s,
                Port = WebPort(s.Spec?.Ports),
                Headless = string.Equals(s.Spec?.ClusterIP, "None", StringComparison.Ordinal),
            })
            .Where(x => x.Port is not null)
            .OrderBy(x => x.Headless)
            .Select(x => new ServiceEndpoint(
                x.Service.Metadata.NamespaceProperty, x.Service.Metadata.Name, x.Port!.Value))];

    /// <summary>The port Prometheus serves its API on: named for the web UI, else 9090.</summary>
    private static int? WebPort(IList<V1ServicePort>? ports)
    {
        if (ports is null || ports.Count == 0)
            return null;

        foreach (var name in new[] { "http-web", "web", "http" })
            if (ports.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal)) is { } named)
                return named.Port;

        return ports.FirstOrDefault(p => p.Port == 9090)?.Port;
    }

    /// <summary>One trivial query — proves the endpoint is a Prometheus and that we may reach it.</summary>
    private async Task<bool> AnswersAsync(ServiceEndpoint endpoint, CancellationToken ct)
    {
        var json = await GetAsync(endpoint, "api/v1/query?query=1", ct).ConfigureAwait(false);
        return json is { } root
               && root.TryGetProperty("status", out var status)
               && status.GetString() == "success";
    }

    private async Task<JsonElement?> GetAsync(ServiceEndpoint endpoint, string path, CancellationToken ct)
    {
        try
        {
            var uri = new Uri(apiServer,
                $"api/v1/namespaces/{endpoint.Namespace}/services/{endpoint.Service}:{endpoint.Port}/proxy/{path}");

            using var response = await http.GetAsync(uri, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return document.RootElement.Clone();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
