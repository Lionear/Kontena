using System.Net;
using System.Text.Json;
using k8s.Models;
using Kontena.Adapters.Kubernetes;
using Xunit;

namespace Kontena.Adapters.Kubernetes.Tests;

public class AlertingDiscoveryTests
{
    private static V1Service Service(string ns, string name, string clusterIp, params (string Name, int Port)[] ports) =>
        new()
        {
            Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = ns },
            Spec = new V1ServiceSpec
            {
                ClusterIP = clusterIp,
                Ports = [.. ports.Select(p => new V1ServicePort { Name = p.Name, Port = p.Port })],
            },
        };

    private static ProxyResponse Json(HttpStatusCode status, string json) =>
        new(status, JsonDocument.Parse(json).RootElement.Clone(), null);

    [Fact]
    public void The_alertmanager_port_is_found_by_name_or_by_9093()
    {
        Assert.Equal(
            9093,
            ApiProxyHttp.Rank([Service("monitoring", "alertmanager-operated", "None", ("web", 9093))], 9093)[0].Port);

        // A chart that remaps the port keeps the name, so the name wins over the well-known number.
        Assert.Equal(
            8080,
            ApiProxyHttp.Rank([Service("monitoring", "alertmanager", "10.0.0.1", ("http", 8080))], 9093)[0].Port);

        // And a service with neither is not a candidate at all rather than a guess.
        Assert.Empty(ApiProxyHttp.Rank([Service("monitoring", "alertmanager", "10.0.0.1", ("grpc", 10901))], 9093));
    }

    [Fact]
    public void A_normal_alertmanager_service_is_preferred_over_the_headless_one()
    {
        // kube-prometheus-stack installs both: alertmanager-operated is headless and exists for the
        // StatefulSet, the release-named one is the supported way in.
        var ranked = ApiProxyHttp.Rank(
        [
            Service("monitoring", "alertmanager-operated", "None", ("http-web", 9093)),
            Service("monitoring", "kps-kube-prometheus-alertmanager", "10.1.2.3", ("http-web", 9093)),
        ], 9093);

        Assert.Equal("kps-kube-prometheus-alertmanager", ranked[0].Service);
    }

    [Fact]
    public void A_service_is_only_believed_once_it_answers_like_the_thing_it_claims_to_be()
    {
        // Alertmanager's /api/v2/status carries a cluster member; Prometheus answers every query
        // with status=success. A service named right that answers neither is not it.
        Assert.True(AlertingDiscovery.IsAlertmanager(
            Json(HttpStatusCode.OK, """{"cluster":{"status":"ready"},"uptime":"1h"}""")));
        Assert.False(AlertingDiscovery.IsAlertmanager(Json(HttpStatusCode.OK, """{"status":"success"}""")));

        Assert.True(AlertingDiscovery.IsPrometheus(
            Json(HttpStatusCode.OK, """{"status":"success","data":{"result":[]}}""")));
        Assert.False(AlertingDiscovery.IsPrometheus(
            Json(HttpStatusCode.OK, """{"status":"error","error":"parse error"}""")));

        // A 200 is not enough on its own — an Ingress or a sidecar will happily return one.
        Assert.False(AlertingDiscovery.IsAlertmanager(new ProxyResponse(HttpStatusCode.OK, null, null)));
    }

    [Fact]
    public void A_refusal_is_a_different_answer_from_an_absence()
    {
        var forbidden = new ProxyResponse(HttpStatusCode.Forbidden, null, null);
        Assert.True(forbidden.Forbidden);
        Assert.False(forbidden.Ok);
        Assert.Contains("services/proxy", forbidden.Describe(), StringComparison.Ordinal);

        var timeout = new ProxyResponse(null, null, "The operation was canceled.");
        Assert.False(timeout.Forbidden);
        Assert.False(timeout.Ok);
        Assert.Equal("The operation was canceled.", timeout.Describe());

        Assert.Contains("404", ((int)HttpStatusCode.NotFound).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains("no such service", new ProxyResponse(HttpStatusCode.NotFound, null, null).Describe(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_proxy_url_is_the_apiserver_service_proxy_and_keeps_the_query_string()
    {
        var proxy = new ApiProxyHttp(new HttpClient(), new Uri("https://10.0.0.1:6443/"));
        var endpoint = new ServiceEndpoint("monitoring", "alertmanager-operated", 9093);

        Assert.Equal(
            "https://10.0.0.1:6443/api/v1/namespaces/monitoring/services/alertmanager-operated:9093/proxy/api/v2/alerts",
            proxy.UriFor(endpoint, "api/v2/alerts").ToString());

        // The query string is the whole reason this is a raw HttpClient and not a generated call.
        Assert.EndsWith(
            "/proxy/api/v1/query?query=up",
            proxy.UriFor(new ServiceEndpoint("monitoring", "prometheus-operated", 9090), "api/v1/query?query=up").ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_empty_state_can_name_every_place_the_search_went()
    {
        // The notice is built from these, so a candidate added here shows up there without anyone
        // remembering to edit a view.
        Assert.Contains("monitoring", AlertingDiscovery.CandidateNamespaces);
        Assert.Contains("observability", AlertingDiscovery.CandidateNamespaces);
        Assert.Contains("app.kubernetes.io/name=alertmanager", AlertingDiscovery.AlertmanagerSelectors);
        Assert.Contains("alertmanager-operated", AlertingDiscovery.AlertmanagerNames);

        Assert.Empty(AlertingProbe.Nothing.LookedFor);
        Assert.Null(AlertingProbe.Nothing.Alertmanager);
        Assert.False(AlertingProbe.Nothing.RuleCrd);
    }

    [Fact]
    public void The_rule_crd_is_the_operator_kind_and_nothing_else()
    {
        Assert.Equal("monitoring.coreos.com", AlertingDiscovery.RuleKind.Group);
        Assert.Equal("PrometheusRule", AlertingDiscovery.RuleKind.Kind);
        Assert.Equal("v1", AlertingDiscovery.RuleKind.Version);
    }
}
