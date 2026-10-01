using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Adapters.Kubernetes;

/// <summary>
/// Watches any kind the cluster serves, custom ones included (KON-483).
/// <para>
/// The typed watchers cover the kinds Kontena has pages for. Everything else — every CRD an operator
/// installed — had no watch at all, so the Resources page showed whatever the cluster said the moment
/// it was opened and kept saying it: a CloudNativePG cluster that had long since come up still read
/// "Waiting for the instances to become active". This is the same watch kubectl uses for any kind,
/// on the path discovery resolves, asking for metadata only: a page re-reads what it shows, and an
/// event only has to say which object moved.
/// </para>
/// </summary>
internal static class ResourceWatch
{
    /// <summary>
    /// Metadata only where the server can (every supported Kubernetes can), whole objects where it cannot
    /// — the event says the same either way.
    /// </summary>
    private const string Accept = "application/json;as=PartialObjectMetadata;g=meta.k8s.io;v=v1, application/json";

    public static async IAsyncEnumerable<ResourceEvent> WatchAsync(
        HttpClient http, Uri baseUri, ApiResourceInfo resource, GroupVersionKind kind, string? ns,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, ResourceTables.RequestUri(baseUri, resource, ns, query: "watch=1"));
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd(Accept);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        // Refused (RBAC) or gone: an empty stream, which a page reads as "no longer live" — true.
        if (!response.IsSuccessStatusCode)
            yield break;

        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(body);

        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            switch (Read(line, kind, ns))
            {
                case { } e:
                    yield return e;
                    break;

                // The server ended the watch in-band (an expired resource version, say). Stopping here
                // is what a closed stream means to every caller.
                case null when IsError(line):
                    yield break;
            }
        }
    }

    /// <summary>One line of a watch stream, or null for a line that is not an object event.</summary>
    internal static ResourceEvent? Read(string line, GroupVersionKind kind, string? fallbackNamespace)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;

        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        var eventType = type switch
        {
            "ADDED" => WatchEventType.Added,
            "MODIFIED" => WatchEventType.Modified,
            "DELETED" => WatchEventType.Deleted,
            _ => (WatchEventType?)null,
        };

        if (eventType is null
            || !root.TryGetProperty("object", out var obj)
            || !obj.TryGetProperty("metadata", out var metadata)
            || !metadata.TryGetProperty("name", out var name)
            || name.GetString() is not { Length: > 0 } objectName)
            return null;

        var ns = metadata.TryGetProperty("namespace", out var n) ? n.GetString() : fallbackNamespace;

        return new ResourceEvent { Type = eventType.Value, Resource = new ResourceRef(kind, ns, objectName) };
    }

    private static bool IsError(string line) => line.Contains("\"type\":\"ERROR\"", StringComparison.Ordinal);
}
