using k8s.Models;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Adapters.Kubernetes;

/// <summary>
/// Finds the workloads that use, or were created by, one object (KON-455).
/// <para>
/// Every signal here holds for any kind, because the alternative does not work. Tools that answer
/// "what uses this" with a table of resource types stop at the types they were taught, and a CRD is by
/// definition one nobody taught them — the author of the CRD decides what refers to what. So this asks
/// only questions Kubernetes answers the same way for everything.
/// </para>
/// <para>
/// The ladder, most trustworthy first. An ownerReference is Kubernetes' own record. A mount is two
/// exact hops — the config object's ownerReference, then the pod spec naming it — and is the shape of
/// the operator pattern, where a custom resource emits a Secret and a workload consumes it. An
/// annotation is a convention, so it comes last and only counts under a key belonging to the object's
/// own API group, which is what stops it matching any annotation holding the same string.
/// </para>
/// </summary>
internal static class ResourceUsers
{
    /// <summary>The kinds a "used by" answer points at. Pods are left out deliberately: they belong to
    /// a workload, and the answer people want is the thing they can edit.</summary>
    internal static readonly GroupVersionKind[] WorkloadKinds =
        [GroupVersionKind.Deployment, GroupVersionKind.StatefulSet, GroupVersionKind.DaemonSet];

    /// <summary>True when one of the owner references points at the object being asked about.</summary>
    internal static bool OwnedBy(IList<V1OwnerReference>? owners, ResourceRef target) =>
        owners?.Any(o =>
            string.Equals(o.Kind, target.Kind.Kind, StringComparison.Ordinal)
            && string.Equals(o.Name, target.Name, StringComparison.Ordinal)
            && SameGroup(o.ApiVersion, target.Kind.Group)) == true;

    /// <summary>
    /// An owner reference carries the full <c>apiVersion</c>, so the group is everything before the
    /// slash — and empty for a core kind, whose apiVersion is bare ("v1").
    /// </summary>
    private static bool SameGroup(string? apiVersion, string group)
    {
        if (apiVersion is null)
            return false;

        var slash = apiVersion.IndexOf('/', StringComparison.Ordinal);
        var owner = slash < 0 ? string.Empty : apiVersion[..slash];

        return string.Equals(owner, group, StringComparison.Ordinal);
    }

    /// <summary>
    /// The annotation key that names this object, or null. The key must belong to the object's own API
    /// group: <c>dragonflydb.io/cluster: my-cache</c> counts, a bare <c>cluster: my-cache</c> does not.
    /// Without that rule any workload annotated with a string that happens to equal the name would be
    /// reported as using it, which is how a helpful list turns into a misleading one.
    /// </summary>
    internal static string? AnnotationNaming(
        IDictionary<string, string>? annotations, ResourceRef target)
    {
        if (annotations is null || target.Kind.Group.Length == 0)
            return null;

        foreach (var (key, value) in annotations)
        {
            if (string.Equals(value, target.Name, StringComparison.Ordinal)
                && key.Contains(target.Kind.Group, StringComparison.OrdinalIgnoreCase))
            {
                return key;
            }
        }

        return null;
    }

    /// <summary>
    /// Every ConfigMap and Secret a pod spec names, as "ConfigMap/x" or "Secret/x" — through volumes,
    /// projected volumes, <c>envFrom</c>, and single <c>valueFrom</c> variables. All four are how a
    /// workload actually consumes what an operator produced, and leaving one out means missing exactly
    /// the workloads that use that one.
    /// </summary>
    internal static IReadOnlySet<string> ConfigReferences(V1PodSpec? spec)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        if (spec is null)
            return names;

        foreach (var volume in spec.Volumes ?? [])
        {
            Add(names, "Secret", volume.Secret?.SecretName);
            Add(names, "ConfigMap", volume.ConfigMap?.Name);

            foreach (var source in volume.Projected?.Sources ?? [])
            {
                Add(names, "Secret", source.Secret?.Name);
                Add(names, "ConfigMap", source.ConfigMap?.Name);
            }
        }

        // Init containers count: a workload that only reads the credentials while starting up is still
        // a workload that stops working when the object behind them goes away.
        foreach (var container in Containers(spec))
        {
            foreach (var from in container.EnvFrom ?? [])
            {
                Add(names, "Secret", from.SecretRef?.Name);
                Add(names, "ConfigMap", from.ConfigMapRef?.Name);
            }

            foreach (var variable in container.Env ?? [])
            {
                Add(names, "Secret", variable.ValueFrom?.SecretKeyRef?.Name);
                Add(names, "ConfigMap", variable.ValueFrom?.ConfigMapKeyRef?.Name);
            }
        }

        return names;
    }

    private static IEnumerable<V1Container> Containers(V1PodSpec spec) =>
        (spec.Containers ?? []).Concat(spec.InitContainers ?? []);

    private static void Add(HashSet<string> names, string kind, string? name)
    {
        if (!string.IsNullOrEmpty(name))
            names.Add($"{kind}/{name}");
    }

    /// <summary>
    /// Decide what, if anything, links one workload to the object — in ladder order, so the strongest
    /// available answer is the one reported.
    /// </summary>
    internal static ResourceUsage? Link(
        ResourceRef workload,
        IList<V1OwnerReference>? owners,
        IDictionary<string, string>? annotations,
        V1PodSpec? spec,
        ResourceRef target,
        IReadOnlyDictionary<string, string> ownedConfig)
    {
        if (OwnedBy(owners, target))
        {
            return new ResourceUsage(
                workload, UsageEvidence.OwnerReference, "ownerReference", OwnedByTarget: true);
        }

        foreach (var reference in ConfigReferences(spec))
        {
            if (ownedConfig.TryGetValue(reference, out var how))
                return new ResourceUsage(workload, UsageEvidence.Mount, how);
        }

        return AnnotationNaming(annotations, target) is { } key
            ? new ResourceUsage(workload, UsageEvidence.Annotation, key)
            : null;
    }
}
