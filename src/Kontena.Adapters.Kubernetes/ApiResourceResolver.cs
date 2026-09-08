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

    /// <summary>
    /// What the definitions say about the custom kinds, keyed "group/Kind". Read once per session like
    /// the discovery cache above, and for the same reason: it does not change under a running app
    /// unless an apply installs a CRD, which already invalidates through <see cref="Invalidate"/>.
    /// </summary>
    private IReadOnlyDictionary<string, CrdFacts>? _definitions;

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
    public void Invalidate(string group, string version)
    {
        _cache.TryRemove($"{group}/{version}", out _);

        // The definitions go with it: an apply that installs a CRD is exactly when this is stale.
        _definitions = null;
    }

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

        var definitions = await DefinitionsAsync(ct).ConfigureAwait(false);
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

                var custom = IsCustom(group);
                var facts = custom && definitions.TryGetValue($"{group}/{resource.Kind}", out var found)
                    ? found
                    : default;

                resources.Add(new ApiResource
                {
                    Kind = new GroupVersionKind(group, version, resource.Kind),
                    Plural = resource.Name,
                    Namespaced = resource.Namespaced,
                    Verbs = [.. resource.Verbs],
                    ShortNames = [.. resource.ShortNames ?? []],
                    Categories = [.. resource.Categories ?? []],
                    Description = facts.Description ?? string.Empty,
                    Source = facts.Source ?? string.Empty,
                    IsCustom = custom,
                });
            }
        }

        return resources;
    }

    /// <summary>What one definition says about itself, beyond what discovery already reports.</summary>
    private readonly record struct CrdFacts(string? Description, string? Source);

    /// <summary>
    /// Read every CustomResourceDefinition, for the two things only they carry: what the kind is for,
    /// and what installed it.
    /// <para>
    /// Best-effort by design. Listing definitions is cluster-scoped, so a user with rights in one
    /// namespace is refused — and that user still has a working picker, one line shorter. A page that
    /// failed to open because an optional subtitle could not be fetched would be the worse trade.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyDictionary<string, CrdFacts>> DefinitionsAsync(CancellationToken ct)
    {
        if (_definitions is { } cached)
            return cached;

        var facts = new Dictionary<string, CrdFacts>(StringComparer.Ordinal);

        try
        {
            var list = await client.ApiextensionsV1
                .ListCustomResourceDefinitionAsync(cancellationToken: ct)
                .ConfigureAwait(false);

            foreach (var crd in list?.Items ?? [])
            {
                if (crd.Spec?.Group is not { Length: > 0 } group
                    || crd.Spec.Names?.Kind is not { Length: > 0 } kind)
                {
                    continue;
                }

                facts[$"{group}/{kind}"] = new CrdFacts(DescriptionOf(crd), SourceOf(crd));
            }
        }
        catch (Exception)
        {
            // No rights, or no apiextensions at all. Both mean "no subtitles", not "no page".
        }

        _definitions = facts;
        return facts;
    }

    /// <summary>
    /// The schema's own description, from the version the cluster stores — the one whose schema is
    /// authoritative when several are served. Trimmed to its first sentence: these run to paragraphs of
    /// reference documentation, and the picker has one line.
    /// </summary>
    private static string? DescriptionOf(V1CustomResourceDefinition crd)
    {
        var versions = crd.Spec.Versions ?? [];
        var version = versions.FirstOrDefault(v => v.Storage) ?? versions.FirstOrDefault(v => v.Served)
            ?? versions.FirstOrDefault();

        if (version?.Schema?.OpenAPIV3Schema?.Description is not { Length: > 0 } description)
            return null;

        return FirstSentence(description);
    }

    private const int DescriptionLimit = 160;

    internal static string FirstSentence(string text)
    {
        // On any whitespace, not just spaces: these descriptions are YAML block scalars and arrive
        // with the line breaks still in them.
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        // ". " rather than ".", so "v1.21.2" and "e.g." do not end the sentence early.
        var stop = collapsed.IndexOf(". ", StringComparison.Ordinal);
        if (stop > 0 && stop < DescriptionLimit)
            return collapsed[..(stop + 1)];

        if (collapsed.Length <= DescriptionLimit)
            return collapsed;

        // Cut on a word rather than mid-word, and say that it was cut.
        var cut = collapsed.LastIndexOf(' ', DescriptionLimit);
        return string.Concat(collapsed.AsSpan(0, cut > 0 ? cut : DescriptionLimit), "…");
    }

    /// <summary>
    /// Who installed it, in the order the answer gets less specific: the Helm release that owns the
    /// definition, then the app it says it is part of, then its own app name.
    /// </summary>
    private static string? SourceOf(V1CustomResourceDefinition crd)
    {
        var labels = crd.Metadata?.Labels;
        var annotations = crd.Metadata?.Annotations;

        return Value(annotations, "meta.helm.sh/release-name")
            ?? Value(labels, "app.kubernetes.io/part-of")
            ?? Value(labels, "app.kubernetes.io/name");
    }

    private static string? Value(IDictionary<string, string>? from, string key) =>
        from is not null && from.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

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
