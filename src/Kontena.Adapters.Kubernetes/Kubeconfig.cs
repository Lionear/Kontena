using k8s;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Adapters.Kubernetes;

/// <summary>
/// Reads the kubeconfig — the entry point for everything else in this adapter. One kubeconfig
/// holds many contexts, and Kontena surfaces each as its own backend in the switcher, so the user
/// picks a cluster the same way they pick an engine.
/// </summary>
public static class Kubeconfig
{
    /// <summary>
    /// Load the contexts from the default kubeconfig (<c>$KUBECONFIG</c>, else <c>~/.kube/config</c>).
    /// Returns an empty list when there is no kubeconfig or it cannot be parsed — no kubeconfig is a
    /// normal state for a machine that only runs containers, not an error worth throwing over.
    /// </summary>
    /// <param name="path">
    /// A specific kubeconfig file, or null for the default one. A cluster config downloaded from a
    /// provider often lives outside <c>~/.kube</c>, and copying it in is a change to the user's setup that
    /// Kontena has no business making (KON-118).
    /// </param>
    public static IReadOnlyList<KubeContext> LoadContexts(string? path = null)
    {
        try
        {
            var config = string.IsNullOrWhiteSpace(path)
                ? KubernetesClientConfiguration.LoadKubeConfig()
                : KubernetesClientConfiguration.LoadKubeConfig(Expand(path));

            var current = config.CurrentContext;

            return
            [
                .. config.Contexts.Select(c => new KubeContext
                {
                    Name = c.Name,
                    Cluster = c.ContextDetails?.Cluster ?? string.Empty,
                    User = c.ContextDetails?.User ?? string.Empty,
                    Namespace = string.IsNullOrEmpty(c.ContextDetails?.Namespace) ? null : c.ContextDetails.Namespace,
                    IsCurrent = c.Name == current,
                }),
            ];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// The contexts whose apiserver is on this machine — <c>kind</c>, <c>minikube</c> with the docker
    /// driver, <c>k3d</c> and friends all point at <c>127.0.0.1</c>. Read straight out of the same file
    /// <see cref="LoadContexts"/> reads, so deciding this costs no connection at all.
    /// <para>
    /// It exists for one reason: <see cref="KubernetesClusterProvider.ProbeTimeout"/>. A cluster across
    /// a WAN needs a long deadline (KON-329), but a probe round costs whatever its slowest member costs
    /// and runs them together, so handing that same deadline to a stopped local cluster would make every
    /// round wait on it. A loopback apiserver either answers in milliseconds or is not running.
    /// </para>
    /// <para>
    /// Loopback only, deliberately: a cluster on a private LAN address may be a VM on this desk or a
    /// company network reached over a VPN, and those are not the same wait at all. Being wrong towards
    /// the longer deadline costs a slower probe round; being wrong the other way marks a reachable
    /// cluster dead, which is the bug this whole line of tickets is about.
    /// </para>
    /// </summary>
    public static IReadOnlySet<string> LoadLoopbackContexts(string? path = null)
    {
        try
        {
            var config = string.IsNullOrWhiteSpace(path)
                ? KubernetesClientConfiguration.LoadKubeConfig()
                : KubernetesClientConfiguration.LoadKubeConfig(Expand(path));

            var loopback = config.Clusters
                .Where(c => IsLoopback(c.ClusterEndpoint?.Server))
                .Select(c => c.Name)
                .ToHashSet(StringComparer.Ordinal);

            return config.Contexts
                .Where(c => c.ContextDetails?.Cluster is { } cluster && loopback.Contains(cluster))
                .Select(c => c.Name)
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception)
        {
            // Same contract as LoadContexts: an unreadable kubeconfig is not an error to throw over.
            // Answering "none are local" only costs those contexts the longer deadline.
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    /// <summary><see cref="Uri.IsLoopback"/> already covers <c>localhost</c>, <c>127.0.0.0/8</c> and <c>::1</c>.</summary>
    private static bool IsLoopback(string? server) =>
        Uri.TryCreate(server, UriKind.Absolute, out var uri) && uri.IsLoopback;

    /// <summary>Build a client configuration for one context, from the default kubeconfig or a named one.</summary>
    internal static KubernetesClientConfiguration ConfigFor(string context, string? path = null) =>
        string.IsNullOrWhiteSpace(path)
            ? KubernetesClientConfiguration.BuildConfigFromConfigFile(currentContext: context)
            : KubernetesClientConfiguration.BuildConfigFromConfigFile(
                kubeconfigPath: Expand(path), currentContext: context);

    /// <summary>
    /// Resolves <c>~</c>, because a path the user typed is far more likely to start with it than one the
    /// system handed us, and the Kubernetes client does not expand it.
    /// </summary>
    /// <remarks>
    /// The separators in the tail are rewritten too. <c>Path.Combine</c> only joins — it leaves the
    /// slashes inside what it is given alone — so <c>~/.kube/config</c> on Windows came out as
    /// <c>C:\Users\me\.kube/config</c>. That opens the file perfectly well, which is why it went
    /// unnoticed, but the string is what the Settings page shows and what identifies a kubeconfig:
    /// two spellings of one file would be two entries.
    /// </remarks>
    public static string Expand(string path)
    {
        var trimmed = path.Trim();
        if (!trimmed.StartsWith('~'))
            return trimmed;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var tail = trimmed.TrimStart('~').TrimStart('/', '\\')
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

        return Path.Combine(home, tail);
    }

    /// <summary>The default kubeconfig Kontena reads without being told to, for showing in the UI.</summary>
    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("KUBECONFIG") is { Length: > 0 } fromEnv
            ? fromEnv
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kube", "config");
}
