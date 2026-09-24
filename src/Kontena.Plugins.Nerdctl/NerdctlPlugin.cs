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
    /// <summary>
    /// What the loader actually uses. <c>PluginLoader</c> instantiates the entry type with
    /// <see cref="Activator.CreateInstance(Type)"/> — no arguments, no host services handed in — so
    /// without this constructor the plugin is discovered, consented to, and then rejected with a
    /// <see cref="MissingMethodException"/>: loadable in every respect except the one that counts.
    /// <para>
    /// The <see cref="IToolRunner"/> overload stays for the tests, which script a fake CLI. Nothing
    /// else can supply one: a plugin has no way to ask the host for a service yet, and inventing a
    /// service-injection contract for one dependency this assembly can construct itself would be a
    /// change to the loader, not to this plugin.
    /// </para>
    /// </summary>
    public NerdctlPlugin()
        : this(new ToolRunner())
    {
    }

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
        Backends = [BackendKind.Engine],

        // The CLI this whole plugin is (KON-438). Declaring it is what gets it onto Settings › Tools
        // with the host's own detection — until now the only place nerdctl's absence showed up was an
        // empty backend list, which reads as "nothing here" rather than "install nerdctl".
        Tools = [NerdctlTool.Definition],
    };

    /// <summary>One provider per containerd namespace — see <see cref="NerdctlEngineProvider.DiscoverAll"/>
    /// for what happens when nerdctl cannot be asked at all.</summary>
    public IEnumerable<IBackendProvider> GetProviders() => NerdctlEngineProvider.DiscoverAll(runner);
}
