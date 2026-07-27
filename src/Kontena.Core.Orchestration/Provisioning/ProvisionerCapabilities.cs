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

    /// <summary>Which container runtime hosts the nodes can be chosen, rather than taken from the tool's default.</summary>
    public bool ChooseRuntime { get; init; }

    /// <summary>A cluster can be paused and resumed without deleting it. minikube can; kind cannot.</summary>
    public bool PauseResume { get; init; }
}
