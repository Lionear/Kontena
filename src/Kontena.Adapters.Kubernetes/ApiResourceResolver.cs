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

    /// <summary>
    /// Forget what one group/version serves. Caching for the session is right until an apply installs
    /// a CRD: from then on the cached answer is not stale by chance, it is a "no such kind" recorded
    /// before the kind existed, and every custom resource in the same bundle would trip over it.
    /// </summary>
    public void Invalidate(string group, string version) => _cache.TryRemove($"{group}/{version}", out _);

    /// <summary>
    /// Everything the cluster serves: the core group plus every API group at its preferred version.
    /// <para>
    /// Subresources ("pods/log") and anything that cannot be listed are left out — the first is not a
    /// kind, the second is a row in a picker that could only ever fail.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<ApiResource>> DiscoverAllAsync(CancellationToken ct = default)
    {
        var groups = new List<(string Group, string Version)> { (string.Empty, "v1") };

        try
        {
            var list = await client.Apis.GetAPIVersionsAsync(ct).ConfigureAwait(false);

            // The preferred version only. Offering every served version of a kind turns one entry into
            // three that show the same objects, and the server is telling us which one it means.
            groups.AddRange(
                from g in list.Groups ?? []
                let version = g.PreferredVersion?.Version ?? g.Versions?.FirstOrDefault()?.Version
                where !string.IsNullOrEmpty(g.Name) && !string.IsNullOrEmpty(version)
                select (g.Name, version!));
        }
        catch (Exception)
        {
            // No group discovery — the core group alone is still worth offering.
        }

        var resources = new List<ApiResource>();

        foreach (var (group, version) in groups)
        {
            foreach (var resource in await ResourcesForAsync(group, version, ct).ConfigureAwait(false))
            {
                if (string.IsNullOrEmpty(resource.Kind)
                    || resource.Name.Contains('/', StringComparison.Ordinal)
                    || resource.Verbs?.Contains("list") != true)
                {
                    continue;
                }

                resources.Add(new ApiResource
                {
                    Kind = new GroupVersionKind(group, version, resource.Kind),
                    Plural = resource.Name,
                    Namespaced = resource.Namespaced,
                    Verbs = [.. resource.Verbs],
                    IsCustom = IsCustom(group),
                });
            }
        }

        return resources;
    }

    /// <summary>
    /// Kubernetes reserves the <c>k8s.io</c> suffix for its own APIs, so anything outside it was added
    /// by whoever installed it — with the exception of the groups that predate that convention and never
    /// got the suffix. Missing those would file Deployments under "custom", which is the one heading
    /// they are not.
    /// </summary>
    private static readonly string[] BuiltInGroups =
        ["apps", "batch", "autoscaling", "policy", "extensions"];

    internal static bool IsCustom(string group) =>
        !string.IsNullOrEmpty(group)
        && !BuiltInGroups.Contains(group, StringComparer.Ordinal)
        && !group.Equals("k8s.io", StringComparison.Ordinal)
        && !group.EndsWith(".k8s.io", StringComparison.Ordinal);

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
