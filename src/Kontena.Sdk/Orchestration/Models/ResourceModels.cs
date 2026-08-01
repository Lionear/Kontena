namespace Kontena.Sdk.Orchestration.Models;

/// <summary>
/// A Kubernetes group/version/kind — the coordinate that identifies a resource type
/// (e.g. <c>apps/v1 Deployment</c>, core <c>v1 Pod</c>). Drives the generic declarative
/// core (<c>WatchAsync</c>, <c>GetManifestAsync</c>) so new kinds — including CRDs — need
/// no new typed method.
/// </summary>
public readonly record struct GroupVersionKind(string Group, string Version, string Kind)
{
    /// <summary>Core group has an empty group name (e.g. Pod, Service, Namespace).</summary>
    public bool IsCoreGroup => string.IsNullOrEmpty(Group);

    public override string ToString() =>
        IsCoreGroup ? $"{Version}/{Kind}" : $"{Group}/{Version}/{Kind}";

    // A few common coordinates, so callers don't hand-spell them.
    public static GroupVersionKind Pod => new(string.Empty, "v1", "Pod");
    public static GroupVersionKind Service => new(string.Empty, "v1", "Service");
    public static GroupVersionKind Namespace => new(string.Empty, "v1", "Namespace");
    public static GroupVersionKind Node => new(string.Empty, "v1", "Node");
    public static GroupVersionKind ConfigMap => new(string.Empty, "v1", "ConfigMap");
    public static GroupVersionKind Secret => new(string.Empty, "v1", "Secret");
    public static GroupVersionKind PersistentVolumeClaim => new(string.Empty, "v1", "PersistentVolumeClaim");
    public static GroupVersionKind PersistentVolume => new(string.Empty, "v1", "PersistentVolume");
    public static GroupVersionKind Ingress => new("networking.k8s.io", "v1", "Ingress");
    public static GroupVersionKind StorageClass => new("storage.k8s.io", "v1", "StorageClass");
    public static GroupVersionKind Deployment => new("apps", "v1", "Deployment");
    public static GroupVersionKind StatefulSet => new("apps", "v1", "StatefulSet");
    public static GroupVersionKind DaemonSet => new("apps", "v1", "DaemonSet");
    public static GroupVersionKind ReplicaSet => new("apps", "v1", "ReplicaSet");
    public static GroupVersionKind Job => new("batch", "v1", "Job");
    public static GroupVersionKind CronJob => new("batch", "v1", "CronJob");

    /// <summary>
    /// The coordinate for a workload kind. Spelled out per kind rather than defaulted: the batch
    /// kinds are not in the <c>apps</c> group, so a fallback to Deployment would address the wrong
    /// resource for a Job or CronJob rather than fail loudly.
    /// </summary>
    public static GroupVersionKind For(WorkloadKind kind) => kind switch
    {
        WorkloadKind.Deployment => Deployment,
        WorkloadKind.StatefulSet => StatefulSet,
        WorkloadKind.DaemonSet => DaemonSet,
        WorkloadKind.ReplicaSet => ReplicaSet,
        WorkloadKind.Job => Job,
        WorkloadKind.CronJob => CronJob,
        _ => Deployment,
    };
}

/// <summary>
/// A concrete, addressable reference to one resource: its kind plus namespace and name.
/// The neutral equivalent of <c>kubectl -n ns get kind name</c>; passed to apply/delete,
/// exec, port-forward, logs, and manifest reads.
/// </summary>
public readonly record struct ResourceRef(GroupVersionKind Kind, string? Namespace, string Name)
{
    public override string ToString() =>
        Namespace is null ? $"{Kind.Kind}/{Name}" : $"{Kind.Kind}/{Name} (ns:{Namespace})";
}

/// <summary>A Kubernetes namespace and a little rollup for the grid.</summary>
public sealed record KubeNamespace
{
    public required string Name { get; init; }

    /// <summary>Phase, typically "Active" or "Terminating".</summary>
    public string Phase { get; init; } = "Active";

    /// <summary>Labels attached to the namespace.</summary>
    public IReadOnlyDictionary<string, string> Labels { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Age since creation.</summary>
    public TimeSpan Age { get; init; }
}
