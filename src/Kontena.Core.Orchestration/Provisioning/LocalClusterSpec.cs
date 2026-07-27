namespace Kontena.Core.Orchestration.Provisioning;

/// <summary>
/// What to create. Everything but the name has a default that produces a working single-node cluster,
/// because that is what most people want and the form should not make them say so.
/// </summary>
/// <param name="Name">Cluster name. Also decides the kubeconfig context, so it has to be unique among
/// the clusters this provisioner already owns.</param>
public sealed record LocalClusterSpec(string Name)
{
    /// <summary>
    /// Kubernetes version for the nodes, e.g. <c>v1.31.0</c>. Null means the tool's own default, which
    /// is the version that release was tested with — a better answer than any we could pick.
    /// </summary>
    public string? KubernetesVersion { get; init; }

    /// <summary>
    /// An explicit node image, overriding <see cref="KubernetesVersion"/>. For an air-gapped mirror or
    /// a digest-pinned image; the version field cannot express either.
    /// </summary>
    public string? NodeImage { get; init; }

    /// <summary>
    /// Control-plane nodes. More than one needs <see cref="ProvisionerCapabilities.HighAvailability"/>
    /// and gives a stacked etcd — useful for rehearsing a control-plane failure, not for capacity.
    /// </summary>
    public int ControlPlaneNodes { get; init; } = 1;

    /// <summary>Worker nodes on top of the control plane. Zero is fine: a single node schedules pods too.</summary>
    public int WorkerNodes { get; init; }

    /// <summary>Host ports to publish. See <see cref="ClusterPortMapping"/> for why this is create-time only.</summary>
    public IReadOnlyList<ClusterPortMapping> PortMappings { get; init; } = [];

    /// <summary>
    /// Prepare the first control-plane node for an ingress controller (the <c>ingress-ready</c> label
    /// its manifests select on). It does not install one — that is a workload, and installing something
    /// nobody asked for is how a tool becomes untrustworthy.
    /// </summary>
    public bool IngressReady { get; init; }

    /// <summary>Which runtime hosts the nodes. See <see cref="LocalClusterRuntime"/>.</summary>
    public LocalClusterRuntime Runtime { get; init; } = LocalClusterRuntime.Default;

    /// <summary>
    /// CPUs for the cluster, or null for the tool's default. Only meaningful where the nodes are a VM
    /// with a fixed size — a container takes what the host has.
    /// </summary>
    public int? Cpus { get; init; }

    /// <summary>Memory in megabytes, or null for the tool's default. Same reasoning as <see cref="Cpus"/>.</summary>
    public int? MemoryMb { get; init; }

    /// <summary>
    /// How long to wait for the control plane to report ready before giving up. Null asks the tool not
    /// to wait at all, which is its own default: it returns as soon as the nodes are up, and the
    /// cluster becomes usable a moment later.
    /// </summary>
    public TimeSpan? ReadyTimeout { get; init; }
}
