using k8s;
using Kontena.Core.Orchestration.Models;

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
    public static IReadOnlyList<KubeContext> LoadContexts()
    {
        try
        {
            var config = KubernetesClientConfiguration.LoadKubeConfig();
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

    /// <summary>Build a client configuration for one context.</summary>
    internal static KubernetesClientConfiguration ConfigFor(string context) =>
        KubernetesClientConfiguration.BuildConfigFromConfigFile(currentContext: context);
}
