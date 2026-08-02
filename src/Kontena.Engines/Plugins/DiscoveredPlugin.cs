using Kontena.Sdk;

namespace Kontena.Engines.Plugins;

/// <summary>What happened to one directory under the plugins root.</summary>
public enum PluginStatus
{
    /// <summary>Loaded, and its providers are in <see cref="DiscoveredPlugin.Providers"/>.</summary>
    Loaded,

    /// <summary>Found and readable, but the user has not agreed to run this build of it.</summary>
    AwaitingConsent,

    /// <summary>Not usable. <see cref="DiscoveredPlugin.Reason"/> says why.</summary>
    Rejected,
}

/// <summary>
/// One directory under the plugins root and what became of it. Failures are values here rather than
/// exceptions: a plugin that cannot load is a thing to report in Settings, not a reason for the app
/// not to start.
/// </summary>
/// <param name="Directory">The directory scanned, so a report can name it.</param>
/// <param name="Manifest">What it said about itself, or null when that could not be read.</param>
/// <param name="Status">The outcome.</param>
/// <param name="Reason">Why, when the outcome is <see cref="PluginStatus.Rejected"/>.</param>
/// <param name="Providers">What it contributed, when it loaded.</param>
public sealed record DiscoveredPlugin(
    string Directory,
    PluginManifest? Manifest,
    PluginStatus Status,
    string? Reason,
    IReadOnlyList<IBackendProvider> Providers);
