
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
    /// One object, as a one-row Table with the whole object riding along (KON-483). The row carries
    /// the printer columns — the same cells the listing shows — and <c>includeObject=Object</c> brings
    /// the metadata and status the detail page reads, so it is one round-trip rather than a Table plus
    /// a GET. Null when the object is not there.
    /// </summary>
    public static async Task<ResourceObject?> GetAsync(
        HttpClient http, Uri baseUri, ApiResourceInfo resource, ResourceRef reference, CancellationToken ct)
    {
        var uri = RequestUri(baseUri, resource, reference.Namespace, reference.Name);

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(uri.AbsoluteUri + "?includeObject=Object"));
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse(TableMediaType));

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        // Anything else that is not a success is a failed read, not an absent object, and the page has
        // to be able to tell the two apart.
        response.EnsureSuccessStatusCode();

        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);

        return ReadObject(json.RootElement, reference);
    }

    internal static ResourceObject? ReadObject(JsonElement table, ResourceRef reference)
    {
        if (!table.TryGetProperty("rows", out var rows) || rows.GetArrayLength() == 0)
            return null;

        var row = rows[0];
        var names = Read(table, reference.Kind, reference.Namespace).Columns.Select(c => c.Name).ToArray();
        var cells = row.TryGetProperty("cells", out var c) ? c.EnumerateArray().Select(Cell).ToArray() : [];

        // The name is the page's title already; repeating it as the first field says nothing.
        var columns = names
            .Zip(cells, (name, value) => new ResourceField(name, value))
            .Where(f => !f.Name.Equals("Name", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var obj = row.TryGetProperty("object", out var o) ? o : default;
        var metadata = obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty("metadata", out var m) ? m : default;
        var status = obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty("status", out var s) ? s : default;

        return new ResourceObject
        {
            Reference = reference,
            Created = Text(metadata, "creationTimestamp") is { Length: > 0 } created
                      && DateTimeOffset.TryParse(created, System.Globalization.CultureInfo.InvariantCulture,
                          System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
                ? at
                : null,
            Labels = Map(metadata, "labels"),
            Annotations = Map(metadata, "annotations"),
            Owners = Owners(metadata, reference.Namespace),
            Columns = columns,
            Status = status.ValueKind == JsonValueKind.Object
                ? [.. status.EnumerateObject()
                    .Where(p => p.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null))
                    .Select(p => new ResourceField(p.Name, Cell(p.Value)))]
                : [],
            Conditions = Conditions(status),
        };
    }

    private static string Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static Dictionary<string, string> Map(JsonElement metadata, string property)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        if (metadata.ValueKind == JsonValueKind.Object
            && metadata.TryGetProperty(property, out var values)
            && values.ValueKind == JsonValueKind.Object)
        {
            foreach (var pair in values.EnumerateObject())
                map[pair.Name] = pair.Value.ValueKind == JsonValueKind.String ? pair.Value.GetString() ?? string.Empty : pair.Value.ToString();
        }

        return map;
    }

    private static ResourceRef[] Owners(JsonElement metadata, string? ns)
    {
        if (metadata.ValueKind != JsonValueKind.Object
            || !metadata.TryGetProperty("ownerReferences", out var owners)
            || owners.ValueKind != JsonValueKind.Array)
            return [];

        return
        [
            .. owners.EnumerateArray()
                .Where(o => Text(o, "kind").Length > 0 && Text(o, "name").Length > 0)
                .Select(o => new ResourceRef(KindOf(Text(o, "apiVersion"), Text(o, "kind")), ns, Text(o, "name"))),
        ];
    }

    /// <summary><c>postgresql.cnpg.io/v1</c> → that group and version; a bare <c>v1</c> is the core group.</summary>
    internal static GroupVersionKind KindOf(string apiVersion, string kind) =>
        apiVersion.Split('/') is [var group, var version]
            ? new GroupVersionKind(group, version, kind)
            : new GroupVersionKind(string.Empty, apiVersion, kind);

    private static ResourceCondition[] Conditions(JsonElement status)
    {
        if (status.ValueKind != JsonValueKind.Object
            || !status.TryGetProperty("conditions", out var conditions)
            || conditions.ValueKind != JsonValueKind.Array)
            return [];

        return
        [
            .. conditions.EnumerateArray()
                .Where(c => Text(c, "type").Length > 0)
                .Select(c => new ResourceCondition(
                    Text(c, "type"),
                    Text(c, "status"),
                    Text(c, "reason"),
                    Text(c, "message"),
                    DateTimeOffset.TryParse(Text(c, "lastTransitionTime"), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
                        ? at
                        : null)),
        ];
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
    /// <param name="query">
    /// What to ask of a collection. Defaults to the Table projection's own needs; a caller with a
    /// different question — <see cref="ResourceCounts"/> asks for one page and a remainder — passes
    /// its own.
    /// </param>
    internal static Uri RequestUri(
        Uri baseUri, ApiResourceInfo resource, string? ns, string? name = null,
        string query = "includeObject=Metadata")
    {
        var root = string.IsNullOrEmpty(resource.Group)
            ? $"api/{resource.Version}"
            : $"apis/{resource.Group}/{resource.Version}";

        var path = resource.Namespaced && !string.IsNullOrEmpty(ns)
            ? $"{root}/namespaces/{Uri.EscapeDataString(ns)}/{resource.Plural}"
            : $"{root}/{resource.Plural}";

        // A base address without its trailing slash would swallow its last segment when combined.
        var rootUri = baseUri.AbsoluteUri.EndsWith('/') ? baseUri : new Uri(baseUri.AbsoluteUri + "/");

        // A query is a listing concern; asking for one object by name never wants one.
        return new Uri(rootUri, string.IsNullOrEmpty(name)
            ? $"{path}?{query}"
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
