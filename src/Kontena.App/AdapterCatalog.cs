using Kontena.Adapters.Apple;
using Kontena.Adapters.Docker;
using Kontena.Adapters.Kubernetes;
using Kontena.Adapters.Podman;
using Kontena.Engines.Plugins;
using Kontena.Sdk;

namespace Kontena.App;

/// <summary>What an extension adds to Kontena, which is what its card is grouped and labelled by.</summary>
public enum AdapterContribution
{
    /// <summary>A CEAL backend — containers on this or another host.</summary>
    ContainerEngine,

    /// <summary>An OAL backend — an orchestrator.</summary>
    Orchestrator,

    /// <summary>Pages rather than a backend, through <see cref="IUiPlugin"/> (KON-331).</summary>
    Tool,
}

/// <summary>
/// One extension as Settings › Extensions shows it (KON-283) — a bundled adapter or a plugin from the
/// user's plugins directory.
/// <para>
/// The unit is the adapter, not the provider. Kubernetes yields one provider per kube-context, so a
/// list of providers would offer the same adapter as many times as the user has contexts, and switching
/// "it" off would mean switching off one context — which the Clusters page already does, and means
/// something else.
/// </para>
/// </summary>
/// <param name="Manifest">What the adapter says about itself.</param>
/// <param name="Contribution">What it adds, which the card shows as its kind tag.</param>
/// <param name="Chip">Its mark and colour, or null to fall back to a letter.</param>
/// <param name="IsBundled">
/// True for an adapter that ships with Kontena. Shown because "built-in" and "installed by you" are
/// different answers to "where did this come from", and only one of them can be removed from disk.
/// </param>
/// <param name="Owns">
/// Whether a backend id belongs to this adapter. Needed to say what switching it off takes away, and to
/// explain afterwards why the backend the user was on is no longer there.
/// </param>
public sealed record AdapterEntry(
    EngineManifest Manifest,
    AdapterContribution Contribution,
    BackendChipStyle? Chip,
    bool IsBundled,
    Func<string, bool> Owns)
{
    public string Id => Manifest.Id;
}

/// <summary>
/// Every extension this installation knows about, bundled or installed (KON-283).
/// <para>
/// Bundled adapters are compile-time referenced and stay that way (KON-281 was dropped), so they cannot
/// be discovered the way a plugin directory is. They are listed here instead — beside the plugins rather
/// than in a second mechanism, because the page has to show them as one list and "which adapters exist"
/// is one question.
/// </para>
/// </summary>
public static class AdapterCatalog
{
    /// <summary>
    /// The adapters that ship with Kontena, in the order the switcher lists their backends.
    /// <para>
    /// Remote Docker engines belong to the Docker adapter rather than to one of their own: a remote is
    /// reached by speaking the Docker Engine API at another host, so switching Docker off takes the
    /// remotes with it. Saying otherwise would leave rows in the switcher that cannot connect.
    /// </para>
    /// </summary>
    public static IReadOnlyList<AdapterEntry> Bundled { get; } =
    [
        new(DockerAdapterModule.Manifest, AdapterContribution.ContainerEngine,
            new BackendChipStyle(DockerBrand.Glyph, DockerBrand.Accent), IsBundled: true,
            backend => Family(backend) is DockerAdapterModule.BackendId or RemoteDockerFamily),

        new(PodmanAdapterModule.Manifest, AdapterContribution.ContainerEngine,
            new BackendChipStyle(PodmanBrand.Glyph, PodmanBrand.Accent), IsBundled: true,
            backend => Family(backend) == PodmanAdapterModule.BackendId),

        new(AppleAdapterModule.Manifest, AdapterContribution.ContainerEngine,
            new BackendChipStyle(AppleBrand.Glyph, AppleBrand.Accent), IsBundled: true,
            backend => Family(backend) == AppleAdapterModule.BackendId),

        new(KubernetesAdapterModule.Manifest, AdapterContribution.Orchestrator,
            new BackendChipStyle(KubernetesBrand.Glyph, KubernetesBrand.Accent), IsBundled: true,
            backend => Family(backend) == KubernetesAdapterModule.BackendId),
    ];

    /// <summary>The family a remote Docker engine's backend id carries (<c>docker-remote:…</c>).</summary>
    private const string RemoteDockerFamily = "docker-remote";

    /// <summary>
    /// Whether this adapter can do anything on the machine it is running on.
    /// <para>
    /// One place, and it is deliberately the only one: the answer comes from
    /// <see cref="EngineManifest.Platforms"/> (KON-280), the same field the loader reads for a plugin,
    /// so a bundled adapter and an installed one are judged by one rule. An adapter that cannot run here
    /// is not listed at all rather than listed and disabled — a Windows machine has no decision to make
    /// about Apple's runtime, and offering one implies it does.
    /// </para>
    /// <para>
    /// The declaration is what does the work: an empty list means "anywhere", so an adapter that only
    /// runs on one operating system is kept out by its manifest saying so and by nothing else (KON-429).
    /// </para>
    /// </summary>
    public static bool RunsOnThisOs(AdapterEntry adapter) =>
        PluginPlatform.SupportsHost(adapter.Manifest.Platforms);

    /// <summary>
    /// Everything to show, bundled first, with what cannot run here left out.
    /// </summary>
    /// <param name="plugins">
    /// What the loader found. Only plugins that loaded are listed: one awaiting consent or rejected has
    /// no on/off decision to offer — the answer it is waiting for is a different question, and Settings
    /// showing a switch for it would suggest otherwise.
    /// </param>
    public static IReadOnlyList<AdapterEntry> All(IReadOnlyList<DiscoveredPlugin> plugins) =>
    [
        .. Bundled.Where(RunsOnThisOs),
        .. plugins.Where(p => p.Status == PluginStatus.Loaded && p.Manifest is not null).Select(FromPlugin),
    ];

    /// <summary>Which adapter a backend id came from, or null when nothing claims it (a demo backend).</summary>
    public static AdapterEntry? OwnerOf(IReadOnlyList<AdapterEntry> adapters, string backend) =>
        adapters.FirstOrDefault(a => a.Owns(backend));

    /// <summary>
    /// A loaded plugin as a card. Its contribution is read from what it actually produced rather than
    /// from a claim in <c>plugin.json</c>: a plugin that declares a backend and ships none would
    /// otherwise be filed under a kind it has nothing in.
    /// </summary>
    private static AdapterEntry FromPlugin(DiscoveredPlugin plugin)
    {
        var manifest = plugin.Manifest!;
        var contribution = plugin.Providers.Count == 0
            ? AdapterContribution.Tool
            : plugin.Providers.Any(p => p.Kind == BackendKind.Cluster)
                ? AdapterContribution.Orchestrator
                : AdapterContribution.ContainerEngine;

        // The plugin's own logo, from the first provider that declared one. A UI-only plugin has no
        // provider to ask, so it falls back to a letter — the host has no artwork for it either.
        var chip = plugin.Providers.Select(p => p.ChipStyle).FirstOrDefault(s => s is not null);

        var ids = plugin.Providers.Select(p => p.Backend).ToHashSet(StringComparer.Ordinal);

        return new AdapterEntry(
            new EngineManifest
            {
                Id = manifest.Id,
                Name = manifest.Name,
                Version = manifest.Version,
                Author = manifest.Author,
                Description = manifest.Description,
                MinSdkVersion = manifest.MinSdkVersion,

                // Read from the assembly rather than from plugin.json, which carries only the names
                // (KON-438). The loader has already refused the plugin if the two disagree, so what is
                // here is both fully described and named in the file the user was shown.
                Tools = plugin.Tools,
            },
            contribution,
            chip,
            IsBundled: false,
            ids.Contains);
    }

    /// <summary>
    /// The family out of a backend id: everything before the first <c>:</c> or <c>@</c>, which is how
    /// the ids are built ("kubernetes:prod-eu-west", "kubernetes@a1b2c3:default"). The same split
    /// <c>BackendChips</c> makes, for the same reason.
    /// </summary>
    private static string Family(string backend)
    {
        var cut = backend.IndexOfAny([':', '@']);
        return cut < 0 ? backend : backend[..cut];
    }
}
