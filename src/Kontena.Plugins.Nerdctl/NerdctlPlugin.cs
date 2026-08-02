using Kontena.Sdk;
using Kontena.Sdk.Tooling;

namespace Kontena.Plugins.Nerdctl;

/// <summary>
/// The plugin's entry point (KON-141): the host's (future) loader discovers this type, reads
/// <see cref="Manifest"/>, and asks <see cref="GetProviders"/> for the backends to add to the
/// switcher. Everything else in this assembly is reached from here or from a provider it hands back.
/// </summary>
public sealed class NerdctlPlugin(IToolRunner runner) : IEnginePlugin
{
    public EngineManifest Manifest => new()
    {
        Id = "com.kontena.nerdctl",
        Name = "nerdctl",
        Version = "0.1.0",
        Author = "Kontena",
        Description = "Reads containerd through the nerdctl CLI, one backend per namespace.",

        // Kept in sync with Directory.Build.props' <Version> by hand: the SDK does not (yet) publish
        // its own version at runtime for a plugin to read (KON-141 built against SDK 0.4.0).
        MinSdkVersion = "0.4.0",
    };

    /// <summary>One provider per containerd namespace — see <see cref="NerdctlEngineProvider.DiscoverAll"/>
    /// for what happens when nerdctl cannot be asked at all.</summary>
    public IEnumerable<IBackendProvider> GetProviders() => NerdctlEngineProvider.DiscoverAll(runner);
}
