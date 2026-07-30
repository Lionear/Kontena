using Kontena.Core;

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
    /// <summary>Stable backend id, e.g. "docker", "podman", "kubernetes:prod-eu-west".</summary>
    string Backend { get; }

    /// <summary>Human-facing name shown in the switcher, e.g. "Docker" or a kube-context name.</summary>
    string DisplayName { get; }

    /// <summary>Single-letter/short chip for the switcher, e.g. "D", "P", "K8s".</summary>
    string Chip { get; }

    /// <summary>
    /// The mark and colour for this backend's chip, or null to fall back to <see cref="Chip"/> as a
    /// letter badge (KON-80). Null is the default so an adapter — including a third-party one — is not
    /// forced to draw a logo it does not have; the demo backends use it deliberately.
    /// </summary>
    BackendChipStyle? ChipStyle => null;

    /// <summary>Which axis this backend belongs to — drives switcher grouping and UI mode.</summary>
    BackendKind Kind { get; }

    /// <summary>Create a fresh backend instance — an <see cref="IContainerEngine"/> for
    /// <see cref="BackendKind.Engine"/>, an <c>IClusterEngine</c> for <see cref="BackendKind.Cluster"/>.</summary>
    IBackend CreateBackend();
}
