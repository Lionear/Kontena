using Kontena.Engines;

namespace Kontena.Sdk;

/// <summary>
/// Entry point that an external Kontena extension implements to contribute container
/// engine backends. A future plugin loader (KON-49) discovers assemblies exporting
/// this type, reads their <see cref="Manifest"/>, and registers their providers with
/// the <see cref="BackendRegistry"/>.
/// <para>
/// This is the whole surface an adapter author needs: reference <c>Kontena.Sdk</c>
/// (which brings the engine-neutral models, the CEAL, and <see cref="IBackendProvider"/>),
/// implement <see cref="IContainerEngine"/> + <see cref="IBackendProvider"/>, and expose
/// them here.
/// </para>
/// </summary>
public interface IEnginePlugin
{
    /// <summary>Static metadata describing this extension.</summary>
    EngineManifest Manifest { get; }

    /// <summary>The engine providers this plugin contributes.</summary>
    IEnumerable<IBackendProvider> GetProviders();
}
