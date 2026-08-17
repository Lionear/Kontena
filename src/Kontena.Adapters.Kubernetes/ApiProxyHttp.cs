using System.Net;
using System.Text;
using System.Text.Json;
using k8s.Models;

namespace Kontena.Adapters.Kubernetes;

/// <summary>A service reachable through the apiserver proxy, and the port to talk to it on.</summary>
/// <param name="Namespace">Namespace the service lives in.</param>
/// <param name="Service">Service name.</param>
/// <param name="Port">Port on the service, not on the pod.</param>
internal sealed record ServiceEndpoint(string Namespace, string Service, int Port)
{
    /// <summary>How the UI names it: <c>monitoring/alertmanager-operated</c>.</summary>
    public override string ToString() => $"{Namespace}/{Service}";
}

/// <summary>
/// What the proxy came back with, or why nothing did.
/// <para>
/// The distinction is the point. A 403 means the credentials are fine and <c>services/proxy</c> is
/// withheld — something an operator can grant. A timeout means the service is there and not
/// answering. Nothing found means neither. Collapsing all three into "unavailable" is how a page
/// ends up telling someone to install what they already installed.
/// </para>
/// </summary>
/// <param name="Status">The HTTP status, or null when the request never got an answer.</param>
/// <param name="Json">The parsed body, when there was one and it was JSON.</param>
/// <param name="Error">Transport-level failure text, when <paramref name="Status"/> is null.</param>
internal sealed record ProxyResponse(HttpStatusCode? Status, JsonElement? Json, string? Error)
{
    public bool Ok => Status is { } s && (int)s is >= 200 and < 300;

    /// <summary>The apiserver refused: authentication or, far more often, no <c>services/proxy</c>.</summary>
    public bool Forbidden => Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;

    /// <summary>One line naming what went wrong, for a notice that has to say something true.</summary>
    public string Describe() => Status switch
    {
        null => Error ?? "the request did not complete",
        HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized =>
            "the cluster refused the request — this needs the services/proxy permission",
        HttpStatusCode.NotFound => "the apiserver has no such service to proxy to",
        var s => $"the service answered {(int)s!} {s}",
    };
}

/// <summary>
/// An <c>HttpClient</c> for services inside the cluster, over the apiserver's own proxy:
/// <c>/api/v1/namespaces/{ns}/services/{svc}:{port}/proxy/…</c>.
/// <para>
/// This is how Kontena reaches anything that only listens on the cluster network, and it is
/// deliberately not a port-forward. A forward needs a free local port, a lifecycle per view, and it
/// collides with ports the user forwarded themselves; the proxy reuses the connection and the
/// credentials that already work, and keeps working on managed clusters where nothing routes
/// inward. It costs one RBAC verb (<c>services/proxy</c>) and goes through the apiserver, so it
/// suits reads a screen makes — not a polling loop.
/// </para>
/// <para>
/// Nothing here is alerting-specific. Alertmanager and Prometheus are its first two callers; any
/// in-cluster HTTP API is the same shape.
/// </para>
/// </summary>
internal sealed class ApiProxyHttp(HttpClient http, Uri apiServer)
{
    /// <summary>The apiserver URL that proxies <paramref name="path"/> to <paramref name="endpoint"/>.</summary>
    public Uri UriFor(ServiceEndpoint endpoint, string path) =>
        new(apiServer, $"api/v1/namespaces/{endpoint.Namespace}/services/{endpoint.Service}:{endpoint.Port}/proxy/{path}");

    public Task<ProxyResponse> GetAsync(ServiceEndpoint endpoint, string path, CancellationToken ct = default) =>
        SendAsync(endpoint, HttpMethod.Get, path, body: null, ct);

    public Task<ProxyResponse> PostAsync(
        ServiceEndpoint endpoint, string path, string jsonBody, CancellationToken ct = default) =>
        SendAsync(endpoint, HttpMethod.Post, path, jsonBody, ct);

    public Task<ProxyResponse> DeleteAsync(ServiceEndpoint endpoint, string path, CancellationToken ct = default) =>
        SendAsync(endpoint, HttpMethod.Delete, path, body: null, ct);

    private async Task<ProxyResponse> SendAsync(
        ServiceEndpoint endpoint, HttpMethod method, string path, string? body, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, UriFor(endpoint, path));
            if (body is not null)
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            return new ProxyResponse(response.StatusCode, await ReadJsonAsync(response, ct).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A cancelled-by-timeout OperationCanceledException lands here too, which is right: to a
            // caller it is a service that did not answer, not a user who changed their mind.
            return new ProxyResponse(null, null, ex.Message);
        }
    }

    /// <summary>The body as JSON, or null when it was empty or was not JSON at all.</summary>
    private static async Task<JsonElement?> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return document.RootElement.Clone();
        }
        catch (Exception)
        {
            // An error page, an empty 200, a proxy that returned HTML — none of them is JSON, and
            // the status already told the caller what it needed.
            return null;
        }
    }

    // ── Finding something to talk to ─────────────────────────────────────────

    /// <summary>
    /// Ranked endpoints for the services given. A service with a cluster IP comes before a headless
    /// one: both proxy, but the headless <c>*-operated</c> service exists alongside a normal one on
    /// every kube-prometheus-stack, and the normal one is the supported way in.
    /// </summary>
    /// <param name="services">Services to rank.</param>
    /// <param name="defaultPort">Port to accept when none of the usual port names is present.</param>
    public static IReadOnlyList<ServiceEndpoint> Rank(IEnumerable<V1Service> services, int defaultPort) =>
        [.. services
            .Where(s => s.Metadata?.Name is not null && s.Metadata.NamespaceProperty is not null)
            .Select(s => new
            {
                Service = s,
                Port = PickPort(s.Spec?.Ports, defaultPort),
                Headless = string.Equals(s.Spec?.ClusterIP, "None", StringComparison.Ordinal),
            })
            .Where(x => x.Port is not null)
            .OrderBy(x => x.Headless)
            .Select(x => new ServiceEndpoint(
                x.Service.Metadata.NamespaceProperty, x.Service.Metadata.Name, x.Port!.Value))];

    /// <summary>
    /// The port a web API is served on: the conventional port name first, then the well-known
    /// number. Named before numbered because a chart may remap the port but keeps the name.
    /// </summary>
    public static int? PickPort(IList<V1ServicePort>? ports, int defaultPort)
    {
        if (ports is null || ports.Count == 0)
            return null;

        foreach (var name in new[] { "http-web", "web", "http" })
            if (ports.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal)) is { } named)
                return named.Port;

        return ports.FirstOrDefault(p => p.Port == defaultPort)?.Port;
    }
}
