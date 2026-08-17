using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Kontena.Adapters.Kubernetes;

/// <summary>
/// Counting a kind without fetching it (KON-395).
/// <para>
/// Kubernetes has no count endpoint, but it does have the two pieces one is made of. A list asked for
/// with <c>limit=1</c> answers with <c>metadata.remainingItemCount</c> — how many objects it did not
/// send — so one object plus that number is the whole count, in a response of a few hundred bytes
/// whatever the cluster holds. Asking for it as
/// <c>PartialObjectMetadataList</c> keeps even that one object down to its name and labels, and tells
/// the API server it never has to render the rest.
/// </para>
/// <para>
/// <c>remainingItemCount</c> is documented as best-effort: the server fills it in when it paginates a
/// list out of etcd, and may leave it unset. So this walks the list in chunks when it is missing —
/// which is the slow path, in metadata rather than objects, rather than a wrong number. Deliberately
/// no <c>resourceVersion=0</c>: that answer comes from the watch cache, which is not paginated and
/// would never carry the count this exists for.
/// </para>
/// </summary>
internal static class ResourceCounts
{
    private const string MetadataMediaType =
        "application/json;as=PartialObjectMetadataList;g=meta.k8s.io;v=v1";

    /// <summary>How many objects to walk per request once the server has declined to do the arithmetic.</summary>
    private const int ChunkSize = 500;

    /// <summary>
    /// The count, or null when this server will not answer in this shape — a caller that gets null
    /// falls back to a listing rather than showing a number nobody stands behind.
    /// </summary>
    public static async Task<int?> TryCountAsync(
        HttpClient http, Uri baseUri, ApiResourceInfo resource, string? ns, CancellationToken ct)
    {
        var total = 0;
        var limit = 1;
        string? next = null;

        while (true)
        {
            var query = $"limit={limit}" + (next is null ? string.Empty : $"&continue={Uri.EscapeDataString(next)}");
            var uri = ResourceTables.RequestUri(baseUri, resource, ns, query: query);

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse(MetadataMediaType));

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return null;

            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);

            var root = json.RootElement;
            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                total += items.GetArrayLength();

            if (!root.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
                return total;

            // The server did the arithmetic: one page plus what it held back is the answer.
            if (metadata.TryGetProperty("remainingItemCount", out var remaining) &&
                remaining.TryGetInt64(out var rest))
                return total + (int)rest;

            next = metadata.TryGetProperty("continue", out var token) ? token.GetString() : null;
            if (string.IsNullOrEmpty(next))
                return total;

            limit = ChunkSize;
        }
    }
}
