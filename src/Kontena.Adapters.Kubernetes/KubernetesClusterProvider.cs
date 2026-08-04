using Kontena.Sdk;

namespace Kontena.Adapters.Kubernetes;

/// <summary>
/// Registers one kube-context as a backend, alongside Docker and Podman. A kubeconfig usually
/// holds several contexts, so <see cref="DiscoverAll"/> yields one provider each and the grouped
/// switcher lists them under "Clusters · Orchestrators".
/// </summary>
public sealed class KubernetesClusterProvider : IBackendProvider
{
    /// <summary>
    /// What a cluster off this machine gets to answer a probe in, against the two seconds
    /// <see cref="IBackendProvider.ProbeTimeout"/> gives a local socket. Same number
    /// <c>RemoteDockerEngineProvider</c> takes, for the same reason: TLS to a host in some region, and
    /// often an <c>exec:</c> credential plugin (<c>gke-gcloud-auth-plugin</c>, <c>aws eks get-token</c>)
    /// started cold before the first call. Cut off at two seconds, such a cluster is unreachable by
    /// construction — it reads as "Not connected" in the switcher no matter how healthy it is (KON-329).
    /// </summary>
    private static readonly TimeSpan OffMachineProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly string _context;
    private readonly string? _kubeconfigPath;
    private readonly bool _loopbackServer;

    /// <param name="context">The kube-context name.</param>
    /// <param name="kubeconfigPath">
    /// The file it came from, or null for the default kubeconfig (KON-118).
    /// </param>
    /// <param name="loopbackServer">
    /// Whether this context's apiserver is on this machine (<c>kind</c>, <c>k3d</c>, minikube on
    /// docker). Only <see cref="ProbeTimeout"/> reads it, and <see cref="DiscoverAll"/> is what fills it
    /// in from the kubeconfig. It defaults to false so that a provider built from a bare context name —
    /// which is how the wizard builds one, only ever to read <see cref="Backend"/> — errs towards the
    /// longer deadline rather than towards declaring a cluster dead.
    /// </param>
    public KubernetesClusterProvider(
        string context, string? kubeconfigPath = null, bool loopbackServer = false)
    {
        _context = context;
        _kubeconfigPath = string.IsNullOrWhiteSpace(kubeconfigPath) ? null : kubeconfigPath;
        _loopbackServer = loopbackServer;
        Chip = ChipFor(context);
    }

    /// <summary>
    /// A cluster crosses a network before it can answer, so it gets a remote's deadline rather than a
    /// local socket's — unless its apiserver is on this machine, where the default is right and a longer
    /// one would be paid by every probe round: they run together, so the round costs whatever its
    /// slowest member costs, and a stopped <c>kind</c> cluster is exactly the member that would hold it.
    /// <para>
    /// The local value is written out rather than inherited because a class that implements the property
    /// cannot reach the interface's default; <c>ClusterProbeBudgetTests</c> compares the two so they
    /// cannot drift apart unnoticed.
    /// </para>
    /// </summary>
    public TimeSpan ProbeTimeout => _loopbackServer ? TimeSpan.FromSeconds(2) : OffMachineProbeTimeout;

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

    /// <summary>
    /// Every context wears the same mark: what the letters used to carry — which context this is — is
    /// the name right next to it, and a chip cannot say "prod-eu-west" in 26 pixels anyway (KON-80).
    /// </summary>
    public BackendChipStyle? ChipStyle => new(KubernetesBrand.Glyph, KubernetesBrand.Accent);

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
        // One extra read per file, not per context: which contexts are local decides only their probe
        // deadline (see ProbeTimeout), and asking that question per provider would re-parse the same
        // kubeconfig once for every context in it.
        var localContexts = Kubeconfig.LoadLoopbackContexts();

        var providers = new List<KubernetesClusterProvider>(
            Kubeconfig.LoadContexts().Select(
                c => new KubernetesClusterProvider(c.Name, null, localContexts.Contains(c.Name))));

        foreach (var path in extraPaths ?? [])
        {
            var localInFile = Kubeconfig.LoadLoopbackContexts(path);

            providers.AddRange(
                Kubeconfig.LoadContexts(path).Select(
                    c => new KubernetesClusterProvider(c.Name, path, localInFile.Contains(c.Name))));
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
