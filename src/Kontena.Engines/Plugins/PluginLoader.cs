using Kontena.Sdk;

namespace Kontena.Engines.Plugins;

/// <summary>
/// Finds plugins on disk and turns the ones the user has agreed to into providers.
/// <para>
/// One root, the user's own directory. KON-279 describes discovery as "shipped + user", but nothing
/// ships in the box: the first plugin is distributed separately (see <c>Plans/plugin-loader.md</c> §5)
/// and moving the four built-in adapters onto this path (KON-281) was dropped. A second root would
/// exist only in tests. Adding one later is a few lines.
/// </para>
/// </summary>
public static class PluginLoader
{
    /// <summary>Where a user's plugins live — the directory <c>SettingsStore</c> already writes into.</summary>
    public static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Lionear", "Kontena", "plugins");

    /// <summary>
    /// Scan <paramref name="root"/> and report every directory in it. Nothing thrown from one directory
    /// reaches the caller — see <see cref="DiscoveredPlugin"/>.
    /// </summary>
    /// <param name="isAllowed">
    /// Whether the user has agreed to run this exact build. A delegate rather than a settings object so
    /// this project keeps knowing nothing about how preferences are stored.
    /// </param>
    public static IReadOnlyList<DiscoveredPlugin> Discover(
        string root, Func<PluginManifest, bool> isAllowed)
    {
        if (!Directory.Exists(root))
            return [];

        try
        {
            return [.. Directory.GetDirectories(root).Order(StringComparer.Ordinal)
                .Select(dir => DiscoverOne(dir, isAllowed))];
        }
        catch
        {
            // If the root cannot be enumerated (permission denied, I/O error, etc.), a directory we
            // cannot list has nothing to report per plugin — an empty list is the honest answer.
            return [];
        }
    }

    private static DiscoveredPlugin DiscoverOne(string directory, Func<PluginManifest, bool> isAllowed)
    {
        try
        {
            var manifest = PluginManifest.Read(Path.Combine(directory, "plugin.json"), out var reason);
            if (manifest is null)
                return new DiscoveredPlugin(directory, null, PluginStatus.Rejected, reason, []);

            if (!isAllowed(manifest))
                return new DiscoveredPlugin(directory, manifest, PluginStatus.AwaitingConsent, null, []);

            var assembly = Path.Combine(directory, manifest.Assembly);
            if (!File.Exists(assembly))
                return new DiscoveredPlugin(
                    directory, manifest, PluginStatus.Rejected, $"No {manifest.Assembly}", []);

            return Load(directory, manifest, assembly);
        }
        catch (Exception ex)
        {
            // Containment is the point: one directory nobody vetted must not cost a start.
            return new DiscoveredPlugin(directory, null, PluginStatus.Rejected, ex.Message, []);
        }
    }

    private static DiscoveredPlugin Load(string directory, PluginManifest manifest, string assemblyPath)
    {
        var assembly = new PluginLoadContext(assemblyPath).LoadFromAssemblyPath(assemblyPath);

        var entry = assembly.GetExportedTypes()
            .FirstOrDefault(t => typeof(IEnginePlugin).IsAssignableFrom(t)
                                 && t is { IsAbstract: false, IsInterface: false });

        if (entry is null)
            return new DiscoveredPlugin(
                directory, manifest, PluginStatus.Rejected, "No IEnginePlugin in " + manifest.Assembly, []);

        if (Activator.CreateInstance(entry) is not IEnginePlugin plugin)
            return new DiscoveredPlugin(
                directory, manifest, PluginStatus.Rejected, entry.FullName + " is not an IEnginePlugin", []);

        return new DiscoveredPlugin(
            directory, manifest, PluginStatus.Loaded, null, [.. plugin.GetProviders()]);
    }
}
