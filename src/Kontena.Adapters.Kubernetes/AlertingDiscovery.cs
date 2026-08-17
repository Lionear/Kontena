using k8s;
using k8s.Models;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Adapters.Kubernetes;

/// <summary>
/// What one discovery pass found, and — just as importantly — where it went looking.
/// <para>
/// The search is part of the answer. A cluster running an Alertmanager under a name Kontena does
/// not know has to be shown the <b>gap</b>, not an empty list, and the only way that notice stays
/// true is for it to be built from the same values the search used.
/// </para>
/// </summary>
/// <param name="Alertmanager">The Alertmanager that answered, or null.</param>
/// <param name="Prometheus">The Prometheus that answered, or null.</param>
/// <param name="RuleCrd">Whether the <c>PrometheusRule</c> CRD is served.</param>
/// <param name="LookedFor">Every selector and name tried, verbatim.</param>
/// <param name="LookedIn">Namespaces the name search covered.</param>
/// <param name="Refusal">Why the search could not complete, when that is the reason it found nothing.</param>
internal sealed record AlertingProbe(
    ServiceEndpoint? Alertmanager,
    ServiceEndpoint? Prometheus,
    bool RuleCrd,
    IReadOnlyList<string> LookedFor,
    IReadOnlyList<string> LookedIn,
    string? Refusal)
{
    public static readonly AlertingProbe Nothing =
        new(null, null, false, [], [], null);
}

/// <summary>
/// Finds the Alertmanager and Prometheus a cluster is running, the same way
/// <see cref="MetricsServerSource"/> and <see cref="KubeletSummarySource"/> are chosen: look, ask,
/// and take the first one that actually answers.
/// <para>
/// Two flags come out of it and they are independent. <see cref="ClusterCapabilities.Alerting"/>
/// needs an Alertmanager that responds; <see cref="ClusterCapabilities.AlertRules"/> needs only the
/// <c>PrometheusRule</c> CRD, so a cluster with the Operator but no reachable Alertmanager can still
/// apply a rule, and a cluster with neither can still export one to a file.
/// </para>
/// </summary>
internal sealed class AlertingDiscovery(IKubernetes client, ApiProxyHttp proxy, ApiResourceResolver resources)
{
    /// <summary>Namespaces the name-based search covers, in the order operators tend to use them.</summary>
    internal static readonly IReadOnlyList<string> CandidateNamespaces =
        ["monitoring", "observability", "kube-prometheus-stack", "prometheus"];

    /// <summary>Label selectors that find an Alertmanager service, most specific first.</summary>
    internal static readonly IReadOnlyList<string> AlertmanagerSelectors =
        ["app.kubernetes.io/name=alertmanager", "app=kube-prometheus-stack-alertmanager", "app=alertmanager"];

    /// <summary>Service names an Alertmanager is installed under when the labels are missing.</summary>
    internal static readonly IReadOnlyList<string> AlertmanagerNames =
        ["alertmanager-operated", "alertmanager", "alertmanager-main", "kube-prometheus-stack-alertmanager"];

    /// <summary>Service names a Prometheus is installed under. Mirrors <see cref="PrometheusSource"/>.</summary>
    internal static readonly IReadOnlyList<string> PrometheusNames =
        ["prometheus-operated", "prometheus", "prometheus-server", "prometheus-k8s"];

    internal const int AlertmanagerPort = 9093;
    internal const int PrometheusPort = 9090;

    /// <summary>The <c>PrometheusRule</c> kind, which existing only if the Operator's CRDs are installed.</summary>
    internal static readonly GroupVersionKind RuleKind =
        new() { Group = "monitoring.coreos.com", Version = "v1", Kind = "PrometheusRule" };

    public async Task<AlertingProbe> ProbeAsync(CancellationToken ct = default)
    {
        var lookedFor = new List<string>();
        lookedFor.AddRange(AlertmanagerSelectors.Select(s => $"services labelled {s}"));
        lookedFor.AddRange(AlertmanagerNames.Select(n => $"a service named {n}"));

        string? refusal = null;
        ServiceEndpoint? alertmanager = null;
        ServiceEndpoint? prometheus = null;

        try
        {
            alertmanager = await FindAsync(
                AlertmanagerSelectors, AlertmanagerNames, AlertmanagerPort, "api/v2/status", IsAlertmanager, ct)
                .ConfigureAwait(false);

            prometheus = await FindAsync(
                PrometheusSource.Selectors, PrometheusNames, PrometheusPort, "api/v1/query?query=1", IsPrometheus, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Listing services is the step a namespaced user is refused at, and that is a different
            // sentence from "nothing is installed" — so it is carried out rather than swallowed.
            refusal = K8sErrors.Map(ex, "cluster").Message;
        }

        return new AlertingProbe(
            alertmanager,
            prometheus,
            await ServesRuleCrdAsync(ct).ConfigureAwait(false),
            lookedFor,
            CandidateNamespaces,
            refusal);
    }

    /// <summary>
    /// Whether the cluster serves <c>PrometheusRule</c>. Asked through the resolver the apply path
    /// already uses, so it is one cached discovery call and not a second opinion about what exists.
    /// </summary>
    private async Task<bool> ServesRuleCrdAsync(CancellationToken ct)
    {
        try
        {
            return await resources.ResolveAsync(RuleKind, ct).ConfigureAwait(false) is not null;
        }
        catch (Exception)
        {
            // A cluster that will not answer discovery cannot be told to apply a rule either, and
            // the export half does not need this flag.
            return false;
        }
    }

    /// <summary>
    /// Candidates by label across the cluster first, then by name in the candidate namespaces —
    /// and every candidate is asked before it is believed. A service can exist and be a different
    /// thing, or exist and refuse us; only an answer settles it.
    /// </summary>
    private async Task<ServiceEndpoint?> FindAsync(
        IReadOnlyList<string> selectors,
        IReadOnlyList<string> names,
        int port,
        string probePath,
        Func<ProxyResponse, bool> answered,
        CancellationToken ct)
    {
        foreach (var candidate in await CandidatesAsync(selectors, names, port, ct).ConfigureAwait(false))
            if (answered(await proxy.GetAsync(candidate, probePath, ct).ConfigureAwait(false)))
                return candidate;

        return null;
    }

    private async Task<IReadOnlyList<ServiceEndpoint>> CandidatesAsync(
        IReadOnlyList<string> selectors, IReadOnlyList<string> names, int port, CancellationToken ct)
    {
        foreach (var selector in selectors)
        {
            var byLabel = await ListAsync(selector, ct).ConfigureAwait(false);
            if (ApiProxyHttp.Rank(byLabel, port) is { Count: > 0 } found)
                return found;
        }

        var byName = await ListAsync(labelSelector: null, ct).ConfigureAwait(false);
        return ApiProxyHttp.Rank(
            byName.Where(s => names.Contains(s.Metadata?.Name, StringComparer.Ordinal)), port);
    }

    /// <summary>
    /// Services cluster-wide, falling back to the candidate namespaces one at a time when the
    /// credentials are namespaced. A user who may only see <c>monitoring</c> is a normal way to run
    /// this, not a broken one — and cluster-wide listing is the call that refuses them.
    /// </summary>
    private async Task<IEnumerable<V1Service>> ListAsync(string? labelSelector, CancellationToken ct)
    {
        try
        {
            var all = await client.CoreV1
                .ListServiceForAllNamespacesAsync(labelSelector: labelSelector, cancellationToken: ct)
                .ConfigureAwait(false);
            return all.Items ?? Enumerable.Empty<V1Service>();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            var found = new List<V1Service>();
            foreach (var ns in CandidateNamespaces)
            {
                try
                {
                    var list = await client.CoreV1
                        .ListNamespacedServiceAsync(ns, labelSelector: labelSelector, cancellationToken: ct)
                        .ConfigureAwait(false);
                    found.AddRange(list.Items ?? []);
                }
                catch (Exception)
                {
                    // A namespace that does not exist, or one this user cannot see. Both are ordinary.
                }
            }

            return found;
        }
    }

    /// <summary>Alertmanager's <c>/api/v2/status</c> returns an object with a <c>cluster</c> member.</summary>
    internal static bool IsAlertmanager(ProxyResponse response) =>
        response.Ok && response.Json is { } root && root.TryGetProperty("cluster", out _);

    /// <summary>Prometheus answers every query with <c>{"status":"success",…}</c>.</summary>
    internal static bool IsPrometheus(ProxyResponse response) =>
        response.Ok && response.Json is { } root
        && root.TryGetProperty("status", out var status) && status.GetString() == "success";
}
