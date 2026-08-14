
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Adapters.Kubernetes;

/// <summary>
/// Lists any kind the way <c>kubectl get</c> does, by asking the API server for a Table (KON-75).
/// <para>
/// Kubernetes will render a listing itself if asked for
/// <c>application/json;as=Table;v=v1;g=meta.k8s.io</c>: it answers with column headers and pre-formatted
/// cells instead of raw objects. That is where <c>kubectl</c>'s columns come from, including the
/// <c>additionalPrinterColumns</c> a CustomResourceDefinition declares — so a resource nobody has ever
/// modelled arrives with the columns its own author chose.
/// </para>
/// <para>
/// The alternative was a column model per kind, which is a promise to keep up with every operator
/// anyone installs. This way Kontena shows what the cluster says, and someone running <c>kubectl</c>
/// against the same cluster sees the same thing rather than a second opinion.
/// </para>
/// </summary>
internal static class ResourceTables
{
    /// <summary>
    /// <c>includeObject=Metadata</c> asks for each row's name and namespace alongside its cells. Without
    /// it a row is text with nothing to act on — no way to open its YAML or delete it.
    /// </summary>
    private const string TableMediaType = "application/json;as=Table;v=v1;g=meta.k8s.io";

    public static async Task<ResourceTable> ListAsync(
        HttpClient http, Uri baseUri, ApiResourceInfo resource, GroupVersionKind kind, string? ns,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, RequestUri(baseUri, resource, ns));
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse(TableMediaType));

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return ResourceTable.Empty;

        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);

        return Read(json.RootElement, kind, ns);
    }

    /// <summary>
    /// Where to ask: <c>/api/v1/...</c> for the core group, <c>/apis/&lt;group&gt;/&lt;version&gt;/...</c>
    /// for the rest, with the namespace segment only where the kind is namespaced. With
    /// <paramref name="name"/> it addresses one object instead of the collection.
    /// <para>
    /// Absolute, built against the cluster's own base address. The client's <c>HttpClient</c> carries the
    /// credentials and the server certificate but no <c>BaseAddress</c>, so a relative path here does not
    /// produce a wrong request — it produces no request at all.
    /// </para>
    /// </summary>
    internal static Uri RequestUri(Uri baseUri, ApiResourceInfo resource, string? ns, string? name = null)
    {
        var root = string.IsNullOrEmpty(resource.Group)
            ? $"api/{resource.Version}"
            : $"apis/{resource.Group}/{resource.Version}";

        var path = resource.Namespaced && !string.IsNullOrEmpty(ns)
            ? $"{root}/namespaces/{Uri.EscapeDataString(ns)}/{resource.Plural}"
            : $"{root}/{resource.Plural}";

        // A base address without its trailing slash would swallow its last segment when combined.
        var rootUri = baseUri.AbsoluteUri.EndsWith('/') ? baseUri : new Uri(baseUri.AbsoluteUri + "/");

        // The Table projection is a listing concern; asking for one object by name never wants it.
        return new Uri(rootUri, string.IsNullOrEmpty(name)
            ? path + "?includeObject=Metadata"
            : $"{path}/{Uri.EscapeDataString(name)}");
    }

    internal static ResourceTable Read(JsonElement table, GroupVersionKind kind, string? fallbackNamespace)
    {
        var columns = new List<ResourceColumn>();

        if (table.TryGetProperty("columnDefinitions", out var definitions))
        {
            foreach (var column in definitions.EnumerateArray())
            {
                var name = column.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                var priority = column.TryGetProperty("priority", out var p) && p.TryGetInt32(out var value) ? value : 0;

                columns.Add(new ResourceColumn(name, priority));
            }
        }

        var rows = new List<ResourceRow>();

        if (table.TryGetProperty("rows", out var rowsElement))
        {
            foreach (var row in rowsElement.EnumerateArray())
            {
                var cells = row.TryGetProperty("cells", out var cellsElement)
                    ? cellsElement.EnumerateArray().Select(Cell).ToArray()
                    : [];

                var name = string.Empty;
                var ns = fallbackNamespace;

                if (row.TryGetProperty("object", out var obj) && obj.TryGetProperty("metadata", out var metadata))
                {
                    if (metadata.TryGetProperty("name", out var n))
                        name = n.GetString() ?? string.Empty;

                    if (metadata.TryGetProperty("namespace", out var m))
                        ns = m.GetString();
                }

                // A row we cannot address is a row whose actions would act on nothing.
                if (name.Length == 0)
                    continue;

                rows.Add(new ResourceRow(new ResourceRef(kind, ns, name), cells));
            }
        }

        return new ResourceTable { Columns = columns, Rows = rows };
    }

    /// <summary>
    /// Cells are whatever JSON the column's type says. Rendered here rather than in the UI so the grid
    /// only ever deals in strings, and so a number does not arrive quoted.
    /// </summary>
    private static string Cell(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.ToString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        _ => value.ToString(),
    };
}
