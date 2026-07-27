namespace Kontena.Core.Orchestration.Provisioning;

/// <summary>
/// Which container runtime hosts the cluster's nodes.
/// </summary>
public enum LocalClusterRuntime
{
    /// <summary>Whatever the tool picks by itself. The right answer on a machine with one runtime.</summary>
    Default,

    /// <summary>Docker.</summary>
    Docker,

    /// <summary>Podman.</summary>
    Podman,

    /// <summary>
    /// A KVM virtual machine (minikube's <c>kvm2</c> driver). Linux only, and a real VM rather than
    /// containers — which is the reason to pick it: it isolates the cluster from the host kernel.
    /// </summary>
    Kvm2,
}
