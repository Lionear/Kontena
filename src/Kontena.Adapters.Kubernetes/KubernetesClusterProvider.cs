using Kontena.Core;
using Kontena.Engines;

namespace Kontena.Adapters.Kubernetes;

/// <summary>
/// Registers one kube-context as a backend, alongside Docker and Podman. A kubeconfig usually
/// holds several contexts, so <see cref="DiscoverAll"/> yields one provider each and the grouped
/// switcher lists them under "Clusters · Orchestrators".
/// </summary>
public sealed class KubernetesClusterProvider : IBackendProvider
{
    private readonly string _context;

    public KubernetesClusterProvider(string context)
    {
        _context = context;
        Chip = ChipFor(context);
    }

    public string Backend => $"{KubernetesAdapterModule.BackendId}:{_context}";
    public string DisplayName => _context;
    public string Chip { get; }
    public BackendKind Kind => BackendKind.Cluster;

    public IBackend CreateBackend() => new KubernetesClusterEngine(_context);

    /// <summary>
    /// One provider per context in the user's kubeconfig. Returns nothing when there is no
    /// kubeconfig, so a machine without Kubernetes simply shows no Clusters group.
    /// </summary>
    public static IReadOnlyList<KubernetesClusterProvider> DiscoverAll() =>
        [.. Kubeconfig.LoadContexts().Select(c => new KubernetesClusterProvider(c.Name))];

    /// <summary>
    /// A short chip from the context name — "kind-kind" → "KIND", "gke_prod_eu" → "GKE". Falls back
    /// to the first three characters so every context gets something legible.
    /// </summary>
    private static string ChipFor(string context)
    {
        var head = context.Split(['-', '_', '/', '.', ' '], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(head))
            return "K8S";

        return head.Length <= 4 ? head.ToUpperInvariant() : head[..3].ToUpperInvariant();
    }
}
