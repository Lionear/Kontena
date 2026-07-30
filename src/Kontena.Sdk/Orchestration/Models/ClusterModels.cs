using Kontena.Sdk.Models;

namespace Kontena.Sdk.Orchestration.Models;

/// <summary>
/// Identity and health of a connected cluster — the OAL counterpart of an engine's info.
/// Derives from <see cref="BackendInfo"/> so the shared switcher/title-bar chrome reads it
/// like any backend, while adding the cluster-only bits the overview screen needs.
/// </summary>
public sealed record ClusterInfo : BackendInfo
{
    /// <summary>Distribution / provider, e.g. "GKE", "EKS", "k3s", "minikube", "kubeadm".</summary>
    public string Distribution { get; init; } = string.Empty;

    /// <summary>Number of nodes in the cluster.</summary>
    public int NodeCount { get; init; }

    /// <summary>The kube-context this info was read through.</summary>
    public string Context { get; init; } = string.Empty;
}

/// <summary>
/// What a cluster backend supports. Mirrors the engine's capability pattern: the UI queries
/// these to hide, disable, or degrade features the active cluster lacks (e.g. no metrics-server
/// → no live pod metrics; a Swarm-style orchestrator → no namespaces/RBAC/CRDs).
/// </summary>
public sealed record ClusterCapabilities
{
    /// <summary>A metrics-server (metrics.k8s.io) is present → live node/pod metrics.</summary>
    public bool Metrics { get; init; }

    /// <summary>Can exec into pod containers.</summary>
    public bool Exec { get; init; }

    /// <summary>Can port-forward to pods/services.</summary>
    public bool PortForward { get; init; }

    /// <summary>Supports the declarative apply/dry-run flow.</summary>
    public bool Apply { get; init; }

    /// <summary>Helm releases can be listed/managed.</summary>
    public bool Helm { get; init; }

    /// <summary>Supports watch/informer streams.</summary>
    public bool Watch { get; init; }

    /// <summary>Custom Resource Definitions can be browsed.</summary>
    public bool Crds { get; init; }
}

/// <summary>
/// A single entry from the kubeconfig — one addressable cluster+user+namespace combination.
/// One <c>IClusterEngine</c> may surface several of these as separate switcher entries.
/// </summary>
public sealed record KubeContext
{
    /// <summary>Context name as it appears in the kubeconfig.</summary>
    public required string Name { get; init; }

    /// <summary>The cluster this context points at.</summary>
    public string Cluster { get; init; } = string.Empty;

    /// <summary>The user/credential used.</summary>
    public string User { get; init; } = string.Empty;

    /// <summary>Default namespace for the context, if set.</summary>
    public string? Namespace { get; init; }

    /// <summary>Whether this is the currently active context.</summary>
    public bool IsCurrent { get; init; }
}
