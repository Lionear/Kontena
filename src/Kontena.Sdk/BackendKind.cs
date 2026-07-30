namespace Kontena.Sdk;

/// <summary>
/// Which axis a backend belongs to. Drives how the switcher groups it ("Container engines"
/// vs "Clusters · Orchestrators") and which UI mode the app enters when it is activated.
/// </summary>
public enum BackendKind
{
    /// <summary>A container engine (CEAL, <c>IContainerEngine</c>) — Docker, Podman, …</summary>
    Engine,

    /// <summary>A cluster orchestrator (OAL, <c>IClusterEngine</c>) — Kubernetes, …</summary>
    Cluster,
}
