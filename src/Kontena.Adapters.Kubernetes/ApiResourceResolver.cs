using System.Collections.Concurrent;
using k8s;
using k8s.Models;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Adapters.Kubernetes;

/// <summary>What the API server says about one resource type — enough to address it generically.</summary>
/// <param name="Group">API group ("" for core).</param>
/// <param name="Version">API version, e.g. "v1".</param>
/// <param name="Plural">Resource path segment, e.g. "deployments".</param>
/// <param name="Namespaced">Whether instances live in a namespace.</param>
internal sealed record ApiResourceInfo(string Group, string Version, string Plural, bool Namespaced);

/// <summary>
/// Resolves a <see cref="GroupVersionKind"/> to the plural path the API server actually uses, by
/// asking it — not by pluralising the kind.
/// <para>
/// Guessing looks fine until it isn't: <c>Ingress</c> → "ingresses", <c>NetworkPolicy</c> →
/// "networkpolicies", <c>Endpoints</c> → "endpoints", and a CRD may declare any plural it likes.
/// Discovery is one cheap call per group/version and is cached for the session, which is also what
/// makes the apply path work for custom resources without special-casing them.
/// </para>
/// </summary>
internal sealed class ApiResourceResolver(IKubernetes client)
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<V1APIResource>> _cache = new(StringComparer.Ordinal);

    /// <summary>Resolve a kind, or null when the cluster does not serve it.</summary>
    public async Task<ApiResourceInfo?> ResolveAsync(GroupVersionKind gvk, CancellationToken ct = default)
    {
        var resources = await ResourcesForAsync(gvk.Group, gvk.Version, ct).ConfigureAwait(false);

        // Subresources ("deployments/status") share the kind, so skip anything with a slash.
        var match = resources.FirstOrDefault(r =>
            string.Equals(r.Kind, gvk.Kind, StringComparison.Ordinal) &&
            !r.Name.Contains('/', StringComparison.Ordinal));

        return match is null
            ? null
            : new ApiResourceInfo(gvk.Group, gvk.Version, match.Name, match.Namespaced);
    }

    private async Task<IReadOnlyList<V1APIResource>> ResourcesForAsync(string group, string version, CancellationToken ct)
    {
        var key = $"{group}/{version}";
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        IReadOnlyList<V1APIResource> resources;
        try
        {
            // The core group lives at /api/v1, not /apis//v1, so it needs its own call.
            var list = string.IsNullOrEmpty(group)
                ? await client.CoreV1.GetAPIResourcesAsync(ct).ConfigureAwait(false)
                : await client.CustomObjects.GetAPIResourcesAsync(group, version, ct).ConfigureAwait(false);

            resources = [.. list.Resources ?? []];
        }
        catch (Exception)
        {
            // An unserved group/version is a "not found", which callers report per resource.
            resources = [];
        }

        _cache[key] = resources;
        return resources;
    }
}
