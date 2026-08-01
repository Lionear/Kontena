namespace Kontena.Sdk.Orchestration.Models;

/// <summary>How a Service is exposed.</summary>
public enum ServiceType
{
    ClusterIp,
    NodePort,
    LoadBalancer,
    ExternalName,
    Headless,
}

/// <summary>One port a Service publishes.</summary>
public readonly record struct ServicePort(string Name, int Port, int TargetPort, int? NodePort, string Protocol);

/// <summary>A neutral Service view for the network grid.</summary>
public sealed record Service
{
    public required string Name { get; init; }
    public required string Namespace { get; init; }

    public ServiceType Type { get; init; } = ServiceType.ClusterIp;

    /// <summary>Cluster IP, or "None" for headless services.</summary>
    public string ClusterIp { get; init; } = string.Empty;

    /// <summary>External IP / hostname for LoadBalancer services, when assigned.</summary>
    public string ExternalIp { get; init; } = string.Empty;

    public IReadOnlyList<ServicePort> Ports { get; init; } = [];

    /// <summary>Selector that maps the service to pods.</summary>
    public IReadOnlyDictionary<string, string> Selector { get; init; } =
        new Dictionary<string, string>();

    public TimeSpan Age { get; init; }
}

/// <summary>One host/path routing rule of an Ingress.</summary>
public readonly record struct IngressRule(string Host, string Path, string ServiceName, int ServicePort);

/// <summary>A neutral Ingress view.</summary>
public sealed record Ingress
{
    public required string Name { get; init; }
    public required string Namespace { get; init; }

    /// <summary>IngressClass, e.g. "nginx", "traefik".</summary>
    public string Class { get; init; } = string.Empty;

    public IReadOnlyList<IngressRule> Rules { get; init; } = [];

    /// <summary>Load-balancer addresses the ingress is reachable at.</summary>
    public IReadOnlyList<string> Addresses { get; init; } = [];

    /// <summary>Hosts covered by TLS.</summary>
    public IReadOnlyList<string> TlsHosts { get; init; } = [];

    public TimeSpan Age { get; init; }
}

/// <summary>What happens to a volume's data once its claim is gone.</summary>
public enum ReclaimPolicy
{
    /// <summary>The volume and its data are deleted with the claim.</summary>
    Delete,

    /// <summary>The volume is kept, unbound and unavailable, until someone deals with it by hand.</summary>
    Retain,

    /// <summary>Deprecated in Kubernetes, and still reported by older clusters.</summary>
    Recycle,
}

/// <summary>Lifecycle phase of a PersistentVolume.</summary>
public enum VolumePhase
{
    /// <summary>Not yet usable.</summary>
    Pending,

    /// <summary>Free, waiting for a claim.</summary>
    Available,

    /// <summary>Claimed.</summary>
    Bound,

    /// <summary>Its claim is gone but the volume is not — a Retain policy leaves it here.</summary>
    Released,

    Failed,
}

/// <summary>
/// A PersistentVolume — the other half of a claim (KON-254).
/// <para>
/// A Bound claim names a volume that, until this existed, could not be looked at anywhere: how big
/// it really is, what happens to the data when the claim goes, and which driver is behind it.
/// </para>
/// </summary>
public sealed record PersistentVolume
{
    public required string Name { get; init; }

    public VolumePhase Phase { get; init; } = VolumePhase.Pending;

    /// <summary>Actual capacity, which is not always what the claim asked for.</summary>
    public long CapacityBytes { get; init; }

    public IReadOnlyList<string> AccessModes { get; init; } = [];

    public ReclaimPolicy ReclaimPolicy { get; init; } = ReclaimPolicy.Delete;

    public string StorageClass { get; init; } = string.Empty;

    /// <summary>The claim bound to it, as "namespace/name"; empty when unbound.</summary>
    public string Claim { get; init; } = string.Empty;

    /// <summary>The CSI driver or in-tree source behind it, e.g. "ebs.csi.aws.com", "hostPath".</summary>
    public string Driver { get; init; } = string.Empty;

    public TimeSpan Age { get; init; }
}

/// <summary>When a volume is provisioned for a claim.</summary>
public enum VolumeBindingMode
{
    /// <summary>As soon as the claim exists.</summary>
    Immediate,

    /// <summary>
    /// Not until a pod needs it — so a claim sitting on Pending is working exactly as designed, and
    /// is the single most common reason someone thinks their storage is broken when it is not.
    /// </summary>
    WaitForFirstConsumer,
}

/// <summary>A StorageClass — the answer to why a claim is or is not being provisioned (KON-254).</summary>
public sealed record StorageClass
{
    public required string Name { get; init; }

    /// <summary>What provisions volumes for this class, e.g. "ebs.csi.aws.com", "rancher.io/local-path".</summary>
    public string Provisioner { get; init; } = string.Empty;

    public ReclaimPolicy ReclaimPolicy { get; init; } = ReclaimPolicy.Delete;

    public VolumeBindingMode BindingMode { get; init; } = VolumeBindingMode.Immediate;

    /// <summary>Whether a claim that names no class gets this one.</summary>
    public bool IsDefault { get; init; }

    /// <summary>Whether a bound volume of this class can be grown.</summary>
    public bool AllowsExpansion { get; init; }

    public TimeSpan Age { get; init; }
}

/// <summary>Bind/lifecycle phase of a PersistentVolumeClaim.</summary>
public enum PvcPhase
{
    Pending,
    Bound,
    Lost,
}

/// <summary>A neutral PersistentVolumeClaim view for the storage grid.</summary>
public sealed record PersistentVolumeClaim
{
    public required string Name { get; init; }
    public required string Namespace { get; init; }

    public PvcPhase Phase { get; init; } = PvcPhase.Pending;

    /// <summary>Bound volume name, when bound.</summary>
    public string Volume { get; init; } = string.Empty;

    /// <summary>Requested capacity in bytes.</summary>
    public long CapacityBytes { get; init; }

    /// <summary>StorageClass backing the claim.</summary>
    public string StorageClass { get; init; } = string.Empty;

    /// <summary>Access modes, e.g. "RWO", "RWX".</summary>
    public IReadOnlyList<string> AccessModes { get; init; } = [];

    public TimeSpan Age { get; init; }
}
