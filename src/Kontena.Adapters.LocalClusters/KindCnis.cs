using Kontena.Sdk.Orchestration.Provisioning;

namespace Kontena.Adapters.LocalClusters;

/// <summary>
/// The CNIs a kind cluster can be given instead of the kindnet it wires in itself (KON-465).
/// <para>
/// Versions are pinned here rather than fetched as "latest", for the same reason
/// <see cref="KindVersions"/> is a maintained list: these are other projects' release artefacts, and a
/// moving reference means the cluster you build today and the one you build next month are not the same
/// cluster — which is the whole point of building one locally. Bumping one is a code change with a diff
/// and a test run behind it.
/// </para>
/// <para>
/// The two projects publish differently, and that difference is the reason
/// <see cref="ClusterManifest.HelmRelease"/> exists: Calico ships one self-contained install manifest
/// per release, Cilium ships only a Helm chart.
/// </para>
/// </summary>
public static class KindCnis
{
    /// <summary>kind's own. Nothing is applied afterwards and nothing is disabled.</summary>
    public const string Kindnet = "kindnet";

    public const string Calico = "calico";

    public const string Cilium = "cilium";

    /// <summary>
    /// Verified on kind v0.31.0 / Kubernetes v1.35.0: the manifest applies server-side in one pass, the
    /// node reports Ready, and a deny-all NetworkPolicy is enforced.
    /// </summary>
    private const string CalicoVersion = "v3.32.2";

    /// <summary>
    /// Verified on the same cluster: the chart renders, applies server-side in one pass, the node reports
    /// Ready, and a deny-all NetworkPolicy is enforced. No leading <c>v</c> — Helm chart versions carry
    /// none, and the URL is built from this.
    /// </summary>
    private const string CiliumVersion = "1.20.1";

    /// <summary>What the create form offers, kind's own first.</summary>
    public static IReadOnlyList<string> Offered { get; } = [Kindnet, Calico, Cilium];

    /// <summary>
    /// What has to be applied for this choice, or null when it is kind's own and there is nothing to
    /// apply. Unknown names read as the default rather than failing: a spec that names a CNI kind cannot
    /// install should still produce a working cluster.
    /// </summary>
    public static ClusterManifest? Manifest(string? cni) => cni?.Trim().ToLowerInvariant() switch
    {
        Calico => new ClusterManifest(
            $"Calico {CalicoVersion}",
            $"https://raw.githubusercontent.com/projectcalico/calico/{CalicoVersion}/manifests/calico.yaml"),

        // Rendered from the chart, with the two values Cilium's own kind guide asks for: the node image
        // is loaded rather than pullable, and kind has no cloud IPAM to borrow.
        Cilium => new ClusterManifest($"Cilium {CiliumVersion}", $"https://helm.cilium.io/cilium-{CiliumVersion}.tgz")
        {
            HelmRelease = "cilium",
            Namespace = "kube-system",
            HelmValues = ["image.pullPolicy=IfNotPresent", "ipam.mode=kubernetes"],
        },

        _ => null,
    };

    /// <summary>
    /// Whether this choice means kind must be told not to install kindnet. Two CNIs on one cluster is not
    /// a fallback — they fight over the same interfaces — so the switch and the manifest are one decision.
    /// </summary>
    public static bool ReplacesDefault(string? cni) => Manifest(cni) is not null;
}
