namespace Kontena.Sdk.Orchestration.Provisioning;

/// <summary>
/// What a machine is for in a remote cluster. The distinction a local provisioner never has to make:
/// kind and minikube own every node they create, so the count is the whole story — here the machines
/// already exist and the spec has to say which one runs the control plane.
/// </summary>
public enum ClusterHostRole
{
    /// <summary>Runs the control plane: API server, scheduler, and a member of the etcd quorum.</summary>
    Controller,

    /// <summary>Runs workloads only.</summary>
    Worker,
}
