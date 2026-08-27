using System.Runtime.CompilerServices;
using Kontena.Sdk;

// The mapper is the risky part of this adapter and is internal by design; the tests reach it here.
[assembly: InternalsVisibleTo("Kontena.Adapters.Kubernetes.Tests")]

namespace Kontena.Adapters.Kubernetes;

/// <summary>
/// Anchor for the Kubernetes adapter (KON-68) — the OAL counterpart of the Docker/Podman adapters.
/// Implements <c>IClusterEngine</c> against a real apiserver through the official
/// <c>KubernetesClient</c>, with one backend per kube-context.
/// </summary>
public static class KubernetesAdapterModule
{
    /// <summary>Backend identifier used by the backend registry; contexts append <c>:name</c>.</summary>
    public const string BackendId = "kubernetes";

    /// <summary>How this adapter describes itself in Settings › Extensions (KON-283).</summary>
    public static EngineManifest Manifest { get; } = new()
    {
        Id = BackendId,
        Name = "Kubernetes",
        Version = "1.0",
        Author = "Kontena",
        Description =
            "Full cluster management — nodes, workloads, config, RBAC, Helm and topology — with one "
            + "backend per kube-context.",
    };
}
