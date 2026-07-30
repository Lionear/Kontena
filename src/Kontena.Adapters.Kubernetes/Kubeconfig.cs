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
    public static string Expand(string path)
    {
        var trimmed = path.Trim();
        if (!trimmed.StartsWith('~'))
            return trimmed;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, trimmed.TrimStart('~').TrimStart('/', '\\'));
    }

    /// <summary>The default kubeconfig Kontena reads without being told to, for showing in the UI.</summary>
    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("KUBECONFIG") is { Length: > 0 } fromEnv
            ? fromEnv
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kube", "config");
}
