namespace Kontena.Core.Versioning;

/// <summary>
/// Which published product a backend's version should be measured against (KON-370).
/// <para>
/// Deliberately a map to someone else's product names rather than a table of our own version facts:
/// Kontena is not the vendor of any of these, and a hand-kept list of support dates would make it the
/// source of truth for something it does not decide — and would go stale between releases.
/// </para>
/// </summary>
public static class BackendProducts
{
    private static readonly Dictionary<string, string> Products = new(StringComparer.OrdinalIgnoreCase)
    {
        ["docker"] = "docker-engine",

        // A remote is its own switcher entry, but it is Docker on someone's server — which is where a
        // daemon is most likely to have been left behind.
        ["docker-remote"] = "docker-engine",

        ["podman"] = "podman",

        // nerdctl reports containerd's ServerVersion, never its own — nerdctl does not name itself in
        // that payload at all (see NerdctlEngine). containerd is therefore both the honest label for
        // the number and the calendar it should be read against.
        ["nerdctl"] = "containerd",

        ["kubernetes"] = "kubernetes",
    };

    /// <summary>
    /// The managed Kubernetes offerings, which each publish their own support window (KON-95). This is
    /// the nuance that split that ticket: an AKS cluster measured against upstream would be called
    /// unsupported about a month before Azure stops supporting it, and a warning that is wrong is worse
    /// than no warning.
    /// <para>
    /// Keyed on what <c>KubernetesClusterEngine</c> already works out from the cluster itself — the
    /// version string for the ones that stamp it, node labels for AKS. Anything else (kind, minikube,
    /// k3s, kubeadm) is upstream Kubernetes and takes the upstream calendar, which for kind and
    /// minikube is not an approximation at all.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> Distributions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GKE"] = "google-kubernetes-engine",
        ["EKS"] = "amazon-eks",
        ["AKS"] = "azure-kubernetes-service",
    };

    /// <summary>
    /// The product for a backend id, or null when nobody publishes a calendar we can read — Apple's
    /// <c>container</c>, or any third-party adapter. Null means the UI says nothing, never that it
    /// guesses.
    /// </summary>
    /// <param name="backend">
    /// A backend id, which may carry a suffix once there can be more than one of something:
    /// <c>nerdctl:&lt;namespace&gt;</c>, <c>kubernetes:&lt;context&gt;</c>, <c>docker-remote:&lt;id&gt;</c>.
    /// </param>
    /// <param name="distribution">
    /// What a cluster says it is — <c>ClusterInfo.Distribution</c>. Only consulted for Kubernetes:
    /// distribution is a cluster's word about itself, and a stray value must not be able to point a
    /// container engine at somebody's Kubernetes calendar.
    /// </param>
    public static string? For(string? backend, string? distribution = null)
    {
        if (string.IsNullOrEmpty(backend))
            return null;

        var product = Products.GetValueOrDefault(backend.Split(':', 2)[0]);

        return product == "kubernetes" && distribution is not null
            ? Distributions.GetValueOrDefault(distribution, product)
            : product;
    }
}
