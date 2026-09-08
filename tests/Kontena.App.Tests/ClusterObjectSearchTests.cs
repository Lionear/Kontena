using Kontena.App.Services;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// Finding an object by name without knowing its type (KON-458). Kubernetes has no search, so this
/// lists every kind and matches client-side — which makes the cost, and what happens when part of the
/// sweep is refused, the things worth pinning down.
/// </summary>
public sealed class ClusterObjectSearchTests
{
    /// <summary>
    /// A cluster whose kinds are known and whose listings are counted. Two functions rather than an
    /// engine, because that is all the sweep asks for.
    /// </summary>
    private sealed class Cluster
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, string?> _namespaces = new(StringComparer.Ordinal);
        private readonly List<string> _searched = [];
        private int _running;

        public int Filler { get; init; }

        public bool Slow { get; init; }

        public string? RefuseKind { get; init; }

        public int PeakConcurrency { get; private set; }

        public IReadOnlyList<string> Searched
        {
            get { lock (_gate) return [.. _searched]; }
        }

        public string? NamespaceFor(string kind)
        {
            lock (_gate) return _namespaces.GetValueOrDefault(kind);
        }

        public Task<IReadOnlyList<ApiResource>> DiscoverAsync(CancellationToken ct)
        {
            var kinds = new List<ApiResource>
            {
                new() { Kind = GroupVersionKind.ConfigMap, Plural = "configmaps", Namespaced = true, Verbs = ["list"] },
                new() { Kind = GroupVersionKind.Secret, Plural = "secrets", Namespaced = true, Verbs = ["list"] },
                new() { Kind = GroupVersionKind.Event, Plural = "events", Namespaced = true, Verbs = ["list"] },
                new() { Kind = GroupVersionKind.StorageClass, Plural = "storageclasses", Verbs = ["list"] },

                // Served but not listable: offering it would only ever fail.
                new() { Kind = new GroupVersionKind("x.io", "v1", "Unlistable"), Plural = "unlistables", Verbs = ["get"] },
            };

            for (var i = 0; i < Filler; i++)
            {
                kinds.Add(new ApiResource
                {
                    Kind = new GroupVersionKind("filler.io", "v1", $"Filler{i}"),
                    Plural = $"filler{i}s",
                    Namespaced = true,
                    Verbs = ["list"],
                });
            }

            return Task.FromResult<IReadOnlyList<ApiResource>>(kinds);
        }

        public async Task<ResourceTable> ListAsync(ApiResource resource, string? ns, CancellationToken ct)
        {
            var running = Interlocked.Increment(ref _running);

            lock (_gate)
            {
                _searched.Add(resource.Kind.Kind);
                _namespaces[resource.Kind.Kind] = ns;
                PeakConcurrency = Math.Max(PeakConcurrency, running);
            }

            try
            {
                if (Slow)
                    await Task.Delay(15, ct);

                if (string.Equals(resource.Kind.Kind, RefuseKind, StringComparison.Ordinal))
                    throw new UnauthorizedAccessException("forbidden");

                return new ResourceTable
                {
                    Columns = [new("Name", 0)],
                    Rows = [new(new ResourceRef(resource.Kind, ns, "payments-config"), ["payments-config"])],
                };
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }

    private static async Task<(List<SearchHit> Hits, int Done, int Total)> SweepAsync(
        Cluster cluster, string term, string? ns = null)
    {
        var hits = new List<SearchHit>();
        var gate = new Lock();
        var done = 0;
        var total = 0;

        await ClusterObjectSearch.SweepAsync(
            cluster.DiscoverAsync, cluster.ListAsync, term, ns,
            found => { lock (gate) hits.AddRange(found); },
            (finished, all) => { lock (gate) { done = Math.Max(done, finished); total = all; } },
            CancellationToken.None);

        return (hits, done, total);
    }

    /// <summary>Against the real fake engine, so the wiring through IClusterEngine is exercised too.</summary>
    private static async Task<List<SearchHit>> SweepFakeAsync(string term)
    {
        var hits = new List<SearchHit>();
        var gate = new Lock();

        await ClusterObjectSearch.SweepAsync(
            new FakeClusterEngine(), term, null,
            found => { lock (gate) hits.AddRange(found); },
            (_, _) => { },
            CancellationToken.None);

        return hits;
    }

    [Fact]
    public async Task An_object_is_found_without_naming_its_type()
    {
        var hits = await SweepFakeAsync("kontena-app-tls");

        Assert.Contains(hits, h => h is { Name: "kontena-app-tls", Kind: "Certificate" });
    }

    /// <summary>
    /// The point of the ticket: one term, hits from more than one kind. "web" is a Pod, a Service and
    /// an Ingress in the fake, which is exactly the shape of not knowing which type the thing you
    /// remember is.
    /// </summary>
    [Fact]
    public async Task A_term_matches_across_types_at_once()
    {
        var hits = await SweepFakeAsync("web");

        Assert.True(hits.Select(h => h.Kind).Distinct().Count() > 1,
            "expected hits from more than one kind, got: "
            + string.Join(", ", hits.Select(h => $"{h.Kind}/{h.Name}")));
    }

    [Fact]
    public async Task Matching_is_case_insensitive_and_partial()
    {
        var hits = await SweepFakeAsync("KONTENA-APP");

        Assert.Contains(hits, h => h.Name == "kontena-app-tls");
    }

    [Fact]
    public async Task A_term_that_names_nothing_finds_nothing()
    {
        Assert.Empty(await SweepFakeAsync("zzz-nothing-is-called-this"));
    }

    /// <summary>An empty box is not a search over the whole cluster.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_term_does_not_sweep_at_all(string term)
    {
        var cluster = new Cluster();

        var (hits, _, total) = await SweepAsync(cluster, term);

        Assert.Empty(hits);
        Assert.Equal(0, total);
        Assert.Empty(cluster.Searched);
    }

    [Fact]
    public async Task Progress_reaches_every_kind_it_set_out_to_search()
    {
        var (_, done, total) = await SweepAsync(new Cluster(), "payments");

        Assert.True(total > 0);
        Assert.Equal(total, done);
    }

    /// <summary>
    /// An Event's name is a generated suffix on the object it describes, and there are usually more of
    /// them than of everything else together. Searching them by name buys noise for the most requests.
    /// </summary>
    [Fact]
    public async Task Events_are_left_out_of_the_sweep()
    {
        var cluster = new Cluster();

        await SweepAsync(cluster, "payments");

        Assert.DoesNotContain("Event", cluster.Searched);
    }

    /// <summary>A kind that cannot be listed is not a request worth spending.</summary>
    [Fact]
    public async Task A_kind_that_cannot_be_listed_is_skipped()
    {
        var cluster = new Cluster();

        await SweepAsync(cluster, "payments");

        Assert.DoesNotContain("Unlistable", cluster.Searched);
    }

    /// <summary>
    /// Rights differ per kind, so a sweep across everything will meet kinds this user cannot read. One
    /// refusal is one kind fewer in the answer, not a failed search.
    /// </summary>
    [Fact]
    public async Task A_kind_the_user_may_not_read_does_not_sink_the_sweep()
    {
        var cluster = new Cluster { RefuseKind = "Secret" };

        var (hits, done, total) = await SweepAsync(cluster, "payments");

        Assert.Contains(hits, h => h.Kind == "ConfigMap");
        Assert.DoesNotContain(hits, h => h.Kind == "Secret");
        Assert.Equal(total, done);
    }

    /// <summary>A cluster-scoped kind must not be asked for inside a namespace, or it answers for nothing.</summary>
    [Fact]
    public async Task A_cluster_scoped_kind_is_listed_without_a_namespace()
    {
        var cluster = new Cluster();

        await SweepAsync(cluster, "payments", ns: "shop");

        Assert.Equal("shop", cluster.NamespaceFor("ConfigMap"));
        Assert.Null(cluster.NamespaceFor("StorageClass"));
    }

    /// <summary>
    /// The cost is the design problem here, so the cap is asserted rather than assumed: a cluster with
    /// more kinds than the cap still finishes, and never has more listings out at once.
    /// </summary>
    [Fact]
    public async Task The_sweep_never_runs_more_listings_at_once_than_the_cap()
    {
        var cluster = new Cluster { Filler = 40, Slow = true };

        var (_, done, total) = await SweepAsync(cluster, "payments");

        Assert.Equal(total, done);
        Assert.True(cluster.PeakConcurrency <= ClusterObjectSearch.Parallelism,
            $"peak was {cluster.PeakConcurrency}");
    }
}
