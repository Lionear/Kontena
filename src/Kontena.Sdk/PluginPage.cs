using Avalonia.Controls;

namespace Kontena.Sdk;

/// <summary>
/// One sidebar entry and the page behind it (KON-331).
/// <para>
/// The control is produced by a factory rather than handed over ready-made, for two reasons: the host
/// builds its sidebar long before anything is navigated to, and constructing a page is the plugin's
/// code running inside the shell's window — which the host wants to do inside its own containment, at
/// a moment where it has somewhere to report a failure.
/// </para>
/// </summary>
/// <param name="Key">
/// Identifies the page within this plugin. The host prefixes it with the plugin id, so two plugins
/// naming a page <c>editor</c> do not collide.
/// </param>
/// <param name="Label">What the sidebar entry reads.</param>
/// <param name="IconKey">
/// Names an icon the host ships (<c>IconBox</c>, <c>IconLayers</c>, …). A plugin cannot add geometry to
/// the host's resources, so a name the host does not know draws no icon rather than failing.
/// </param>
/// <param name="CreateView">
/// Builds the page, given what the host lends it. Called on the UI thread each time the entry is
/// opened, and never before — so <see cref="IPluginHost.Cluster"/> is whatever is open at that moment
/// rather than whatever was open at startup.
/// </param>
public sealed record PluginPage(
    string Key,
    string Label,
    string IconKey,
    Func<IPluginHost, Control> CreateView);
