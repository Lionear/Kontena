using System.Net;
using System.Net.Http;
using Kontena.Adapters.Kubernetes;

namespace Kontena.Adapters.Kubernetes.Tests;

/// <summary>
/// Counting a kind without fetching it (KON-395).
/// <para>
/// The overview's tiles are integers, and reading them off full listings is what made the page cost
/// the whole cluster every time anything on it moved. The trick is the API server's own
/// <c>remainingItemCount</c>: ask for one object and it says how many more there were. What can go
/// wrong is a server that leaves the field out — documented as best-effort — so the fallback is the
/// half of this worth testing.
/// </para>
/// </summary>
public sealed class ResourceCountTests
{
    private static readonly Uri Cluster = new("https://10.0.0.1:6443/");
    private static readonly ApiResourceInfo Pods = new(string.Empty, "v1", "pods", Namespaced: true);

    /// <summary>Answers each request in turn, and remembers what was asked.</summary>
    private sealed class Server(params string[] pages) : HttpMessageHandler
    {
        private int _answered;

        public List<Uri> Asked { get; } = [];

        public List<string> Accepted { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked.Add(request.RequestUri!);
            Accepted.Add(request.Headers.Accept.ToString());

            var body = _answered < pages.Length ? pages[_answered++] : "{}";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private static async Task<(int? Count, Server Server)> CountAsync(params string[] pages)
    {
        var server = new Server(pages);
        using var http = new HttpClient(server);

        return (await ResourceCounts.TryCountAsync(http, Cluster, Pods, ns: null, ct: default), server);
    }

    [Fact]
    public async Task One_object_plus_what_the_server_held_back_is_the_count()
    {
        var (count, server) = await CountAsync("""
        { "metadata": { "continue": "eyJ2IjoibWV0YS5rOHMu", "remainingItemCount": 4211 }, "items": [ { "metadata": { "name": "api-7d9c" } } ] }
        """);

        Assert.Equal(4212, count);

        // The whole point: one request, and one object in it, on a cluster with four thousand pods.
        var asked = Assert.Single(server.Asked);
        Assert.Contains("limit=1", asked.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("resourceVersion", asked.Query, StringComparison.Ordinal);
        Assert.Contains("PartialObjectMetadataList", server.Accepted[0], StringComparison.Ordinal);
    }

    /// <summary>A list that fits in one page has nothing left over, and says so by omission.</summary>
    [Fact]
    public async Task A_short_list_is_counted_by_what_came_back()
    {
        var (count, server) = await CountAsync("""
        { "metadata": {}, "items": [] }
        """);

        Assert.Equal(0, count);
        Assert.Single(server.Asked);
    }

    /// <summary>
    /// The fallback. <c>remainingItemCount</c> is best-effort, so a server that omits it while still
    /// paginating has to be walked — in metadata rather than objects, and in chunks rather than one at
    /// a time, which is why the second request asks for more than the first.
    /// </summary>
    [Fact]
    public async Task A_server_that_does_not_do_the_arithmetic_is_walked_in_chunks()
    {
        var (count, server) = await CountAsync(
            """{ "metadata": { "continue": "page-2" }, "items": [ { "metadata": { "name": "a" } } ] }""",
            """{ "metadata": { "continue": "page-3" }, "items": [ { "metadata": { "name": "b" } }, { "metadata": { "name": "c" } } ] }""",
            """{ "metadata": {}, "items": [ { "metadata": { "name": "d" } } ] }""");

        Assert.Equal(4, count);
        Assert.Equal(3, server.Asked.Count);
        Assert.Contains("limit=500", server.Asked[1].Query, StringComparison.Ordinal);
        Assert.Contains("continue=page-2", server.Asked[1].Query, StringComparison.Ordinal);
    }

    /// <summary>
    /// A server that refuses the projection gets no opinion from this: null is "ask someone else",
    /// and the engine falls back to a listing rather than showing a number nobody stands behind.
    /// </summary>
    [Fact]
    public async Task A_refusal_is_not_a_zero()
    {
        var server = new RefusingServer();
        using var http = new HttpClient(server);

        Assert.Null(await ResourceCounts.TryCountAsync(http, Cluster, Pods, ns: null, ct: default));
    }

    private sealed class RefusingServer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotAcceptable));
    }
}
