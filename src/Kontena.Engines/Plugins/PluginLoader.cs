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

            if (SdkTooOld(manifest.MinSdkVersion, out var sdkReason))
                return new DiscoveredPlugin(directory, manifest, PluginStatus.Rejected, sdkReason, []);

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

        // The user agreed to what plugin.json said. Code that describes itself differently is not what
        // was agreed to — and until signing lands, this is the only thing tying the two together.
        var declared = plugin.Manifest;
        if (declared.Id != manifest.Id || declared.Version != manifest.Version)
            return new DiscoveredPlugin(
                directory, manifest, PluginStatus.Rejected,
                $"plugin.json says {manifest.Id} {manifest.Version}, the assembly says "
                + $"{declared.Id} {declared.Version}", []);

        return new DiscoveredPlugin(
            directory, manifest, PluginStatus.Loaded, null, [.. plugin.GetProviders()]);
    }

    /// <summary>
    /// Whether the host's <c>Kontena.Sdk</c> is older than what the plugin asks for.
    /// <para>
    /// One number, and it is the SDK assembly's own. The plugin compiles against
    /// <c>Kontena.Sdk.dll</c> and is handed the host's copy at run time, so that assembly <em>is</em>
    /// the contract; a second, hand-maintained "host API version" would be a separate claim about the
    /// same fact, and the two would drift.
    /// </para>
    /// <para>
    /// A floor only. It does not catch a much newer host that dropped something the plugin uses — that
    /// surfaces as a <c>MissingMethodException</c> at the call. The fix is a semver major rule, and it
    /// can be added later against manifests written today, because <c>MinSdkVersion</c> already records
    /// what each plugin was built against. Nothing is lost by waiting, and there is no major 2 yet.
    /// </para>
    /// </summary>
    private static bool SdkTooOld(string minSdkVersion, out string? reason)
    {
        reason = null;

        if (string.IsNullOrWhiteSpace(minSdkVersion))
            return false;

        if (!Version.TryParse(minSdkVersion, out var required))
        {
            reason = $"Unreadable MinSdkVersion '{minSdkVersion}'";
            return true;
        }

        var host = typeof(IEnginePlugin).Assembly.GetName().Version;
        if (host is not null && required <= host)
            return false;

        reason = $"Needs Kontena.Sdk {minSdkVersion}, this build has {host?.ToString() ?? "none"}";
        return true;
    }
}
