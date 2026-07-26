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
    private readonly string? _kubeconfigPath;

    /// <param name="context">The kube-context name.</param>
    /// <param name="kubeconfigPath">
    /// The file it came from, or null for the default kubeconfig (KON-118).
    /// </param>
    public KubernetesClusterProvider(string context, string? kubeconfigPath = null)
    {
        _context = context;
        _kubeconfigPath = string.IsNullOrWhiteSpace(kubeconfigPath) ? null : kubeconfigPath;
        Chip = ChipFor(context);
    }

    /// <summary>
    /// Stable id. A context name is only unique within one kubeconfig — two files can both hold
    /// <c>default</c> — so a non-default file contributes a short hash of its path. Hashed rather than
    /// embedded because this id ends up in settings, and a file path is not something to spread around.
    /// </summary>
    public string Backend => _kubeconfigPath is null
        ? $"{KubernetesAdapterModule.BackendId}:{_context}"
        : $"{KubernetesAdapterModule.BackendId}@{ShortHash(_kubeconfigPath)}:{_context}";

    public string DisplayName => _context;

    /// <summary>
    /// The kubeconfig this context came from, or null for the default one (KON-122). Two files can both
    /// hold a context called <c>default</c>, so the file is the only thing telling them apart in a list.
    /// </summary>
    public string? KubeconfigPath => _kubeconfigPath;
    public string Chip { get; }
    public BackendKind Kind => BackendKind.Cluster;

    public IBackend CreateBackend() => new KubernetesClusterEngine(_context, _kubeconfigPath);

    /// <summary>
    /// One provider per context, from the default kubeconfig plus any files the user added. Returns
    /// nothing when there is no kubeconfig, so a machine without Kubernetes simply shows no Clusters group.
    /// </summary>
    /// <param name="extraPaths">
    /// Kubeconfig files beyond the default one. A path that cannot be read yields no contexts rather than
    /// throwing: a config on a disconnected drive should cost its own entries, not the whole switcher.
    /// </param>
    public static IReadOnlyList<KubernetesClusterProvider> DiscoverAll(IReadOnlyList<string>? extraPaths = null)
    {
        var providers = new List<KubernetesClusterProvider>(
            Kubeconfig.LoadContexts().Select(c => new KubernetesClusterProvider(c.Name)));

        foreach (var path in extraPaths ?? [])
        {
            providers.AddRange(
                Kubeconfig.LoadContexts(path).Select(c => new KubernetesClusterProvider(c.Name, path)));
        }

        return providers;
    }

    private static string ShortHash(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(bytes)[..8];
    }

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
