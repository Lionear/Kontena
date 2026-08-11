namespace Kontena.Sdk;

/// <summary>Result of probing a provider for availability.</summary>
/// <param name="Provider">The provider that was probed.</param>
/// <param name="Connected">True when the backend answered a ping.</param>
/// <param name="Detail">Version/endpoint when connected, or a short reason otherwise.</param>
/// <param name="Version">
/// The version the backend reported, on its own. <see cref="Detail"/> is a line to show a person and
/// cannot be taken apart again reliably — it is the endpoint alone when there was no version — so the
/// number is kept here rather than parsed back out of it (KON-370).
/// </param>
/// <param name="Distribution">
/// What a cluster says it is — "GKE", "AKS", "k3s", "kind". Null for a container engine, which has no
/// such thing. Carried here because the managed Kubernetes offerings each publish their own support
/// window, and the cluster is the only thing that knows which one it is (KON-95).
/// </param>
public sealed record BackendProbe(
    IBackendProvider Provider,
    bool Connected,
    string? Detail,
    string? Version = null,
    string? Distribution = null);
