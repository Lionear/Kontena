namespace Kontena.Sdk;

/// <summary>
/// Entry point for an extension that contributes a place to work rather than a backend (KON-331).
/// The loader discovers it beside <see cref="IEnginePlugin"/>: a plugin may implement either, or both
/// on the same type.
/// <para>
/// This is tier C from KON-87 — the plugin brings its own controls, and the host only decides where
/// they hang. There is no declarative page description here on purpose: the first plugin to need this
/// (Manifest Studio, KON-286) is an editor with a completion popup and a diff view, and a description
/// language rich enough to express that is a second UI framework.
/// </para>
/// </summary>
public interface IUiPlugin
{
    /// <summary>Static metadata describing this extension. The same manifest <see cref="IEnginePlugin"/>
    /// declares, so a plugin implementing both describes itself once.</summary>
    EngineManifest Manifest { get; }

    /// <summary>The pages this plugin contributes, in the order they should appear.</summary>
    IEnumerable<PluginPage> GetPages();
}
