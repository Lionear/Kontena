namespace Kontena.Core.Orchestration.Provisioning;

/// <summary>
/// What one provisioner can be asked for. Same discipline as <c>ClusterCapabilities</c>: model the
/// union, expose the intersection, degrade gracefully — a field the tool does not support is left out
/// of the form instead of being sent and rejected.
/// </summary>
public sealed record ProvisionerCapabilities
{
    /// <summary>More than one node can be asked for (kind: yes, as extra containers).</summary>
    public bool MultiNode { get; init; }

    /// <summary>Several control-plane nodes, i.e. a stacked HA control plane.</summary>
    public bool HighAvailability { get; init; }

    /// <summary>Host ports can be mapped into the cluster at create time.</summary>
    public bool PortMappings { get; init; }

    /// <summary>The nodes can be prepared for an ingress controller (the <c>ingress-ready</c> label).</summary>
    public bool IngressReady { get; init; }

    /// <summary>A specific Kubernetes version can be chosen for the nodes.</summary>
    public bool KubernetesVersion { get; init; }

    /// <summary>
    /// A node image can be named outright. Only where the version list cannot be complete: kind boots
    /// a prebuilt image and there is no way to enumerate what exists, so the field is the way to ask
    /// for anything the offered list does not cover (KON-144). minikube takes a version, not an image.
    /// </summary>
    public bool NodeImage { get; init; }

    /// <summary>
    /// Which runtimes or drivers this provisioner can host nodes on, or empty when it takes whatever
    /// the tool picks. A list rather than a flag: kind runs on Docker or Podman, minikube adds its own
    /// drivers, and the form should offer exactly what the chosen tool accepts.
    /// </summary>
    public IReadOnlyList<LocalClusterRuntime> Runtimes { get; init; } = [];

    /// <summary>CPU and memory can be set per cluster. A VM has to be told; a container does not.</summary>
    public bool Resources { get; init; }

    /// <summary>
    /// A cluster can be stopped and started again without deleting it. minikube can; kind cannot —
    /// stopping its node containers behind its back is not the same thing, because the control plane
    /// comes back believing no time passed.
    /// </summary>
    public bool StartStop { get; init; }
}
