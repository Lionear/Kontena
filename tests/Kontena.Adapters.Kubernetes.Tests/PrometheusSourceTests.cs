using System.Text.Json;
using k8s.Models;
using Kontena.Adapters.Kubernetes;
using Kontena.Sdk.Orchestration;

namespace Kontena.Adapters.Kubernetes.Tests;

/// <summary>
/// The parts of the Prometheus history source that can be pinned without a cluster (KON-345): the
/// PromQL it builds, the response it reads back, and which service it decides to talk to.
/// </summary>
public sealed class PrometheusSourceTests
{
    // ── Query building ───────────────────────────────────────────────────────

    [Fact]
    public void The_cpu_query_excludes_the_rollup_and_pause_series()
    {
        // Both exclusions matter: container="" is the pod-level rollup and container="POD" is the
        // pause container. Leave either in and every pod reads as using twice what it does.
        var query = PrometheusSource.QueryFor(UsageTarget.Pod("payments", "api-7d4f9"), UsageMetric.Cpu, TimeSpan.FromMinutes(2))!;

        Assert.Contains("container!=\"\"", query, StringComparison.Ordinal);
        Assert.Contains("container!=\"POD\"", query, StringComparison.Ordinal);
        Assert.Contains("namespace=\"payments\"", query, StringComparison.Ordinal);
        Assert.Contains("pod=\"api-7d4f9\"", query, StringComparison.Ordinal);
        Assert.Contains("[120s]", query, StringComparison.Ordinal);

        // Kontena counts milli-cores; the series is in cores.
        Assert.Contains("* 1000", query, StringComparison.Ordinal);
    }

    [Fact]
    public void The_memory_query_is_a_plain_sum_with_no_rate()
    {
        var query = PrometheusSource.QueryFor(UsageTarget.Pod("payments", "api-7d4f9"), UsageMetric.Memory, TimeSpan.FromMinutes(2))!;

        Assert.Contains("container_memory_working_set_bytes", query, StringComparison.Ordinal);
        Assert.DoesNotContain("rate(", query, StringComparison.Ordinal);
        Assert.DoesNotContain("* 1000", query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(15, 15)]      // 15 minutes → floor, not 7s
    [InlineData(60, 30)]      // an hour at 120 points
    [InlineData(1440, 720)]   // a day
    public void The_step_never_asks_for_finer_than_the_scrape_interval(int rangeMinutes, int expectedSeconds)
    {
        var step = PrometheusSource.StepFor(TimeSpan.FromMinutes(rangeMinutes));

        Assert.Equal(expectedSeconds, (int)step.TotalSeconds);
    }

    [Fact]
    public void The_rate_window_always_spans_more_than_one_scrape()
    {
        // A rate over a window shorter than two scrapes is empty, which would draw a CPU chart with
        // no line and no explanation.
        foreach (var minutes in new[] { 5, 15, 60, 1440, 10080 })
        {
            var step = PrometheusSource.StepFor(TimeSpan.FromMinutes(minutes));
            var window = PrometheusSource.RateWindow(step);

            Assert.True(window >= TimeSpan.FromMinutes(2), $"{minutes}m: window {window}");
            Assert.True(window >= step * 2, $"{minutes}m: window {window} against step {step}");
        }
    }

    [Theory]
    [InlineData("payments-api-7d4f9", true)]
    [InlineData("kube-system", true)]
    [InlineData("a.b.c", true)]
    [InlineData("pod\" or up{", false)]
    [InlineData("UPPER", false)]
    [InlineData("", false)]
    public void Only_names_a_kubernetes_object_could_have_reach_the_query(string name, bool expected) =>
        Assert.Equal(expected, PrometheusSource.IsSafeName(name));

    // ── Response parsing ─────────────────────────────────────────────────────

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void A_matrix_response_becomes_samples()
    {
        var samples = PrometheusSource.ParseMatrix(Json("""
            {"status":"success","data":{"resultType":"matrix","result":[
              {"metric":{},"values":[[1786000000,"0.125"],[1786000030,"0.5"]]}
            ]}}
            """));

        Assert.Equal(2, samples.Count);
        Assert.Equal(0.125, samples[0].Value);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1786000030), samples[1].At);
    }

    [Fact]
    public void A_failed_or_shapeless_response_is_no_samples_rather_than_a_throw()
    {
        Assert.Empty(PrometheusSource.ParseMatrix(Json("""{"status":"error","errorType":"bad_data"}""")));
        Assert.Empty(PrometheusSource.ParseMatrix(Json("""{"status":"success","data":{"result":[]}}""")));
        Assert.Empty(PrometheusSource.ParseMatrix(Json("""{"status":"success"}""")));
        Assert.Empty(PrometheusSource.ParseMatrix(Json("""{}""")));
    }

    [Fact]
    public void Nan_and_infinity_are_dropped_rather_than_drawn()
    {
        // Prometheus sends values as strings precisely so these survive JSON. Charting a NaN
        // collapses the whole band around it.
        var samples = PrometheusSource.ParseMatrix(Json("""
            {"status":"success","data":{"result":[
              {"metric":{},"values":[[1786000000,"NaN"],[1786000030,"1.5"],[1786000060,"+Inf"]]}
            ]}}
            """));

        Assert.Single(samples);
        Assert.Equal(1.5, samples[0].Value);
    }

    // ── Discovery ────────────────────────────────────────────────────────────

    private static V1Service Service(string ns, string name, string? clusterIp, params (string Name, int Port)[] ports) =>
        new()
        {
            Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = ns },
            Spec = new V1ServiceSpec
            {
                ClusterIP = clusterIp,
                Ports = [.. ports.Select(p => new V1ServicePort { Name = p.Name, Port = p.Port })],
            },
        };

    [Fact]
    public void A_normal_service_is_preferred_over_the_headless_one()
    {
        // Every kube-prometheus-stack has both: prometheus-operated is headless and exists for the
        // StatefulSet, while the release-named service is the supported way in.
        var candidates = ApiProxyHttp.Rank(
        [
            Service("monitoring", "prometheus-operated", "None", ("web", 9090)),
            Service("monitoring", "kps-kube-prometheus-prometheus", "10.1.2.3", ("http-web", 9090)),
        ], 9090);

        Assert.Equal(2, candidates.Count);
        Assert.Equal("kps-kube-prometheus-prometheus", candidates[0].Service);
        Assert.Equal("monitoring", candidates[0].Namespace);
        Assert.Equal(9090, candidates[0].Port);
    }

    [Fact]
    public void A_service_with_no_web_port_is_not_a_candidate()
    {
        var candidates = ApiProxyHttp.Rank(
        [
            Service("monitoring", "prometheus-grpc", "10.1.2.4", ("grpc", 10901)),
        ], 9090);

        Assert.Empty(candidates);
    }

    [Fact]
    public void An_older_kube_prometheus_stack_is_found_by_label_rather_than_by_luck()
    {
        // A real 41.7.3 install carries app=kube-prometheus-stack-prometheus and none of the
        // app.kubernetes.io/name labels. Without this selector it was only found because its name
        // happens to end in "-prometheus", which is a coincidence of the release name.
        Assert.Contains("app=kube-prometheus-stack-prometheus", PrometheusSource.Selectors);

        // And it is tried before the generic app=prometheus, which that install does not carry.
        Assert.True(
            PrometheusSource.Selectors.ToList().IndexOf("app=kube-prometheus-stack-prometheus")
            < PrometheusSource.Selectors.ToList().IndexOf("app=prometheus"));
    }

    [Fact]
    public void The_port_is_found_by_name_or_by_9090()
    {
        Assert.Equal(9090, ApiProxyHttp.Rank([Service("m", "p", "10.0.0.1", ("web", 9090))], 9090)[0].Port);
        Assert.Equal(80, ApiProxyHttp.Rank([Service("m", "p", "10.0.0.1", ("http", 80))], 9090)[0].Port);
        Assert.Equal(9090, ApiProxyHttp.Rank([Service("m", "p", "10.0.0.1", ("unnamed", 9090))], 9090)[0].Port);
    }
}
