using Kontena.Adapters.Docker;
using Kontena.Adapters.Kubernetes;
using Kontena.Adapters.Podman;
using Kontena.Core.Models;
using Kontena.Engines;
using Kontena.Engines.Fakes;

namespace Kontena.App;

/// <summary>
/// The single place that decides which backends Kontena offers. Both startup and the runtime
/// "demo backends" toggle build their provider list here, so the two can never disagree about
/// what a demo backend is.
/// </summary>
public static class BackendCatalog
{
    /// <summary>
    /// Whether demo backends may be offered at all. They exist for development and screenshots and
    /// are never shipped to users: available in a debug build, or opted into from a release build
    /// for demos (<c>KONTENA_FAKE_ENGINE=1</c>).
    /// </summary>
    public static bool DemoAllowed =>
#if DEBUG
        true;
#else
        Environment.GetEnvironmentVariable("KONTENA_FAKE_ENGINE") == "1";
#endif

    /// <summary>What <see cref="KontenaSettings.ShowDemoBackends"/> means when it has not been set:
    /// on where demo backends are permitted, off otherwise.</summary>
    public static bool DemoDefault => DemoAllowed;

    /// <summary>Resolve the stored preference against the build, so a release build never shows demo
    /// backends because a development settings file said so.</summary>
    public static bool ShouldIncludeDemo(bool? preference) =>
        DemoAllowed && (preference ?? DemoDefault);

    /// <summary>
    /// Build the provider list. Real backends always come first so the switcher lists them above the
    /// demo entries.
    /// </summary>
    /// <param name="remotes">
    /// Engines on other hosts, from settings (KON-46). Listed after the local ones: the switcher reads
    /// top-down and what is on this machine is what someone is usually looking for.
    /// </param>
    /// <param name="kubeconfigPaths">
    /// Kubeconfig files the user added beyond the default one (KON-118).
    /// </param>
    /// <param name="showsCluster">
    /// Whether a discovered cluster belongs in the switcher (KON-120). Null includes everything, which is
    /// what discovery itself needs. A cluster the user did not choose is left out here rather than
    /// filtered further up: a provider that exists gets probed, and a cluster nobody asked for should not
    /// be contacted at all.
    /// </param>
    public static List<IBackendProvider> Build(
        bool includeDemo,
        IReadOnlyList<RemoteEngine>? remotes = null,
        IReadOnlyList<string>? kubeconfigPaths = null,
        Func<string, bool>? showsCluster = null)
    {
        var providers = new List<IBackendProvider>
        {
            new DockerEngineProvider(),
            new PodmanEngineProvider(),
        };

        // A misconfigured remote is skipped rather than added as an entry that cannot connect: the
        // Settings page is where its problem is explained, and the switcher is not the place to argue.
        foreach (var remote in remotes ?? [])
        {
            if (remote.Problem is null)
                providers.Add(new RemoteDockerEngineProvider(remote));
        }

        // One cluster backend per chosen kube-context. Yields nothing when there is no kubeconfig, so a
        // machine that only runs containers simply shows no Clusters group.
        providers.AddRange(DiscoverClusters(kubeconfigPaths)
            .Where(p => showsCluster is null || showsCluster(p.Backend)));

        if (includeDemo && DemoAllowed)
        {
            providers.Add(new FakeEngineProvider());
            providers.Add(new FakeClusterProvider("prod-eu-west", "GKE"));
            providers.Add(new FakeClusterProvider("staging", "EKS"));
            providers.Add(new FakeClusterProvider("minikube", "MK"));
        }

        return providers;
    }

    /// <summary>
    /// Every cluster in every kubeconfig Kontena reads, chosen or not (KON-120). Files only — nothing is
    /// contacted — so this is safe to call to find out what is on offer.
    /// </summary>
    public static IReadOnlyList<IBackendProvider> DiscoverClusters(
        IReadOnlyList<string>? kubeconfigPaths = null) =>
        [.. KubernetesClusterProvider.DiscoverAll(kubeconfigPaths)];
}
