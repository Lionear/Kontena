using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Services;

/// <summary>One object whose name matched, and where it lives.</summary>
public sealed record SearchHit(ResourceRef Reference)
{
    public string Name => Reference.Name;

    public string Kind => Reference.Kind.Kind;

    /// <summary>The group, so two kinds of the same name stay apart in a list of mixed types.</summary>
    public string Group => Reference.Kind.IsCoreGroup ? "core" : Reference.Kind.Group;

    public string? Namespace => Reference.Namespace;
}

/// <summary>
/// Finds objects by name across every kind the cluster serves (KON-458).
/// <para>
/// Kubernetes has no search. There is no endpoint that answers "what is called payments", and
/// <c>fieldSelector</c> matches one name exactly, on one kind, which is not the question being asked.
/// The only way to answer it is to list each kind and match client-side — so the cost, not the
/// matching, is what this class is mostly about.
/// </para>
/// <para>
/// Everything the cluster serves, not a list of kinds Kontena knows: the complaint that asked for this
/// was about custom resources, so a sweep that skips the ones nobody modelled misses exactly the
/// objects it exists to find.
/// </para>
/// </summary>
public static class ClusterObjectSearch
{
    /// <summary>
    /// How many listings are in flight at once. The connection pool is shared with everything else the
    /// window is doing, and a sweep that saturates it makes the rest of the app stop answering — the
    /// failure KON-413 was opened for. Eight is enough to hide the latency of a slow kind without
    /// being the reason a page below it is late.
    /// </summary>
    public const int Parallelism = 8;

    /// <summary>
    /// Kinds a name search should not spend a request on. An Event's name is a generated suffix on the
    /// object it describes, so matching it by name finds noise; and there are usually more Events in a
    /// cluster than everything else put together.
    /// </summary>
    private static bool WorthSearching(ApiResource resource) =>
        resource.CanList && resource.Kind.Kind is not "Event";

    /// <summary>
    /// Sweep the cluster for objects whose name contains <paramref name="term"/>.
    /// </summary>
    /// <param name="onHits">
    /// Called as each kind comes back, on whatever thread the listing completed on — the caller
    /// marshals. Progressive by design: a sweep over a hundred kinds takes long enough that holding
    /// every result until the last one arrives would read as a hang.
    /// </param>
    /// <param name="onProgress">Kinds finished and kinds total, for a visible "how far along" line.</param>
    public static Task SweepAsync(
        IClusterEngine cluster,
        string term,
        string? ns,
        Action<IReadOnlyList<SearchHit>> onHits,
        Action<int, int> onProgress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cluster);

        return SweepAsync(
            token => cluster.DiscoverResourcesAsync(token).AsTask(),
            (resource, scope, token) => cluster.ListTableAsync(resource.Kind, scope, token).AsTask(),
            term, ns, onHits, onProgress, ct);
    }

    /// <summary>
    /// The sweep itself, over the two things it actually needs: what kinds exist, and how to list one.
    /// <para>
    /// Not an <see cref="IClusterEngine"/>, because it does not need one — and a unit that asks for
    /// forty members to use two cannot be tested without forty members' worth of stand-in.
    /// </para>
    /// </summary>
    internal static async Task SweepAsync(
        Func<CancellationToken, Task<IReadOnlyList<ApiResource>>> discover,
        Func<ApiResource, string?, CancellationToken, Task<ResourceTable>> list,
        string term,
        string? ns,
        Action<IReadOnlyList<SearchHit>> onHits,
        Action<int, int> onProgress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(discover);
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(onHits);
        ArgumentNullException.ThrowIfNull(onProgress);

        if (string.IsNullOrWhiteSpace(term))
            return;

        var kinds = (await discover(ct).ConfigureAwait(false))
            .Where(WorthSearching)
            .ToArray();

        var done = 0;
        onProgress(0, kinds.Length);

        await Parallel.ForEachAsync(
            kinds,
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
            async (resource, token) =>
            {
                var hits = await ListAsync(list, resource, term, ns, token).ConfigureAwait(false);

                // Progress counts the kind either way. A kind this user may not read is still a kind
                // the sweep is finished with, and a bar that stalls on every 403 is a bar that lies.
                onProgress(Interlocked.Increment(ref done), kinds.Length);

                if (hits.Count > 0)
                    onHits(hits);
            }).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<SearchHit>> ListAsync(
        Func<ApiResource, string?, CancellationToken, Task<ResourceTable>> list,
        ApiResource resource, string term, string? ns, CancellationToken ct)
    {
        try
        {
            // A cluster-scoped kind is never asked for inside a namespace, or the server answers for
            // nothing. Narrowing to the picked namespace is what keeps this affordable.
            var table = await list(resource, resource.Namespaced ? ns : null, ct).ConfigureAwait(false);

            return
            [
                .. table.Rows
                    .Where(row => row.Reference.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
                    .Select(row => new SearchHit(row.Reference)),
            ];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Rights differ per kind, and a sweep across everything will meet kinds this user cannot
            // read. One refusal is not a failed search — it is one kind fewer in the answer.
            return [];
        }
    }
}
