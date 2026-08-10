using Kontena.Sdk.Orchestration;

namespace Kontena.Sdk;

/// <summary>
/// What the host lends a plugin page while it is being built (KON-331).
/// <para>
/// Handed to <see cref="PluginPage.CreateView"/> rather than to <see cref="IUiPlugin.GetPages"/>,
/// because the answers change: the sidebar is built once at startup, when nothing is connected, and a
/// page is built every time it is opened — which is where "the cluster the user is in" is a fact
/// rather than a guess.
/// </para>
/// <para>
/// Deliberately one member. A plugin that needs a cluster is what the first one needs; every further
/// capability belongs to the manifest that declares it and the consent that grants it (KON-79), not to
/// an interface that quietly grows until it is the whole app.
/// </para>
/// </summary>
public interface IPluginHost
{
    /// <summary>
    /// The cluster the user has open, or null when they are on a container engine or nothing is
    /// connected. A page that needs one has to say so on screen rather than assume it — switching
    /// backends is ordinary, and the shell rebuilds the page when it happens.
    /// </summary>
    IClusterEngine? Cluster { get; }
}
