namespace Kontena.Engines;

/// <summary>
/// Describes and creates a backend. This is the extensibility seam shared by both axes of
/// Kontena: container engines today (Docker, Podman — CEAL) and cluster orchestrators later
/// (Kubernetes — OAL), plus future store-installed adapters. The <see cref="BackendRegistry"/>
/// discovers providers and probes them for availability.
/// <para>
/// Today every provider yields an <see cref="IContainerEngine"/>; when the OAL lands, cluster
/// providers will yield an <c>IClusterEngine</c> instead, and one provider may surface several
/// context entries (e.g. kubeconfig contexts). The switcher groups them (Engines / Clusters).
/// </para>
/// </summary>
public interface IBackendProvider
{
    /// <summary>Stable backend id, e.g. "docker", "podman".</summary>
    string Backend { get; }

    /// <summary>Human-facing name shown in the switcher, e.g. "Docker".</summary>
    string DisplayName { get; }

    /// <summary>Single-letter chip for the switcher, e.g. "D".</summary>
    string Chip { get; }

    /// <summary>Create a fresh engine instance for this backend.</summary>
    IContainerEngine CreateEngine();
}
