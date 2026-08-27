using Kontena.Adapters.Apple;
using Kontena.Adapters.Docker;
using Kontena.Adapters.Kubernetes;
using Kontena.Adapters.Podman;
using Kontena.App.Services;
using Kontena.Sdk.Models;
using Kontena.Sdk;
using Kontena.Engines.Fakes;
using Kontena.Core.Models;

namespace Kontena.App;

/// <summary>
/// The single place that decides which backends Kontena offers. Both startup and the runtime
/// "demo backends" toggle build their provider list here, so the two can never disagree about
/// what a demo backend is.
/// </summary>
public static class BackendCatalog
{
    /// <summary>
    /// <see cref="Build"/>'s own signature — the seam a test rebuilds through instead of the real
    /// Docker/Podman engines. A rebuild-triggering test (KON-306) only needs to reach a settings page
    /// again, not probe a real socket to do it.
    /// </summary>
    public delegate List<IBackendProvider> CatalogBuilder(
        bool includeDemo,
        IReadOnlyList<RemoteEngine>? remotes,
        IReadOnlyList<string>? kubeconfigPaths,
        Func<string, bool>? showsCluster,
        Func<string, bool>? adapterEnabled);

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

    private static readonly List<(string AdapterId, IBackendProvider Provider)> Plugins = [];

    /// <summary>
    /// Backends contributed by loaded plugins. Set once at startup and once more after the user agrees
    /// to something new (see <c>MainWindowViewModel.AskPluginConsent</c>) — never per
    /// <see cref="Build"/>, which runs again on every settings change. Loading a plugin directory twice
    /// would mean a second <c>AssemblyLoadContext</c> over the same files, so where the providers live
    /// has to outlast the call that builds the list.
    /// </summary>
    public static IReadOnlyList<IBackendProvider> PluginProviders => [.. Plugins.Select(p => p.Provider)];

    /// <summary>
    /// Add what the loader produced. Adds rather than replaces, because the loader runs twice and the
    /// second run only knows about what consent just unlocked. A backend id already present is skipped:
    /// a directory is only ever loaded once, so a repeat means the same providers, not new ones.
    /// </summary>
    /// <param name="adapterId">
    /// The plugin these came from, kept beside them so <see cref="Build"/> can leave out an adapter the
    /// user switched off (KON-283). Recovering it from the backend ids afterwards would mean a second
    /// table that has to agree with the loader's.
    /// </param>
    public static void SetPluginProviders(string adapterId, IEnumerable<IBackendProvider> providers)
    {
        foreach (var provider in providers)
        {
            if (!Plugins.Any(p => p.Provider.Backend == provider.Backend))
                Plugins.Add((adapterId, provider));
        }
    }

    /// <summary>Forget every plugin provider. For tests, which must not leak into one another.</summary>
    internal static void ResetPluginProviders() => Plugins.Clear();

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
    /// <param name="adapterEnabled">
    /// Whether an adapter the user can switch off in Settings › Extensions is switched on (KON-283).
    /// Null means every adapter, which is what discovery and the tests want. Filtered here rather than
    /// after the fact, because this is the one place that decides what Kontena offers: a switched-off
    /// adapter should never be built, probed, or reachable from the switcher, and a filter anywhere else
    /// is one every future caller has to remember.
    /// </param>
    public static List<IBackendProvider> Build(
        bool includeDemo,
        IReadOnlyList<RemoteEngine>? remotes = null,
        IReadOnlyList<string>? kubeconfigPaths = null,
        Func<string, bool>? showsCluster = null,
        Func<string, bool>? adapterEnabled = null)
    {
        bool Enabled(string adapter) => adapterEnabled is null || adapterEnabled(adapter);

        var providers = new List<IBackendProvider>();

        if (Enabled(DockerAdapterModule.BackendId))
            providers.Add(new DockerEngineProvider());

        if (Enabled(PodmanAdapterModule.BackendId))
            providers.Add(new PodmanEngineProvider());

        // Apple's native runtime (KON-31). Unlike the two above it is not offered unasked on every
        // machine: its `IsInstalled` is false off macOS and false without the binary, so it appears
        // where it can exist and nowhere else. Listing it always, the way Docker and Podman are
        // listed, would put a permanently unreachable row in every Windows and Linux switcher.
        if (Enabled(AppleAdapterModule.BackendId))
            providers.Add(new AppleEngineProvider());

        // A misconfigured remote is skipped rather than added as an entry that cannot connect: the
        // Settings page is where its problem is explained, and the switcher is not the place to argue.
        // A remote speaks the Docker Engine API at another host, so switching that adapter off takes
        // the remotes with it rather than leaving rows nothing can serve.
        foreach (var remote in remotes ?? [])
        {
            if (remote.Problem is null && Enabled(DockerAdapterModule.BackendId))
                providers.Add(new RemoteDockerEngineProvider(remote, SshPasswordPrompt.For(remote)));
        }

        // After what is on this machine, before the clusters: the switcher reads top-down and a plugin
        // backend is still an engine on this host.
        providers.AddRange(Plugins.Where(p => Enabled(p.AdapterId)).Select(p => p.Provider));

        // One cluster backend per chosen kube-context. Yields nothing when there is no kubeconfig, so a
        // machine that only runs containers simply shows no Clusters group.
        if (Enabled(KubernetesAdapterModule.BackendId))
        {
            providers.AddRange(DiscoverClusters(kubeconfigPaths)
                .Where(p => showsCluster is null || showsCluster(p.Backend)));
        }

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
