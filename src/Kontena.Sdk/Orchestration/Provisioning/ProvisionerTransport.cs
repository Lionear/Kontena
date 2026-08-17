namespace Kontena.Sdk.Orchestration.Provisioning;

/// <summary>
/// How a provisioner reaches the machines it installs on. Decides what a host in
/// <see cref="RemoteClusterSpec.Hosts"/> has to be given before a rollout can start (KON-233).
/// </summary>
public enum ProvisionerTransport
{
    /// <summary>
    /// It does not reach out at all — the tool runs here and the cluster comes up here. The default,
    /// so kind and minikube say nothing and mean this.
    /// </summary>
    Local,

    /// <summary>An SSH session per host, which is how kubeadm and k0s are driven.</summary>
    Ssh,

    /// <summary>
    /// The machine's own API rather than a shell on it — Talos, whose nodes have no shell to SSH into.
    /// </summary>
    MachineApi,
}
