using System.Security.Cryptography;
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
    /// Whether the user has agreed to run these exact bytes. A delegate rather than a settings object so
    /// this project keeps knowing nothing about how preferences are stored.
    /// </param>
    public static IReadOnlyList<DiscoveredPlugin> Discover(
        string root, Func<PluginCandidate, bool> isAllowed)
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

    private static DiscoveredPlugin DiscoverOne(string directory, Func<PluginCandidate, bool> isAllowed)
    {
        try
        {
            var manifest = PluginManifest.Read(Path.Combine(directory, "plugin.json"), out var reason);
            if (manifest is null)
                return new DiscoveredPlugin(directory, null, PluginStatus.Rejected, reason, []);

            // GetFileName strips any directory component: an absolute path in the manifest would
            // otherwise replace `directory` outright, and "../.." would escape it. Resolved here rather
            // than after the consent check because the digest below is of this file.
            var assembly = Path.Combine(directory, Path.GetFileName(manifest.Assembly));

            // Ahead of the consent question, where this used to sit behind it: the question is whether
            // to run these bytes, and a directory with no assembly has none to ask about. Leaving it
            // AwaitingConsent would put the same unanswerable prompt up on every launch.
            if (!File.Exists(assembly))
            {
                return new DiscoveredPlugin(
                    directory, manifest, PluginStatus.Rejected, $"No {manifest.Assembly}", []);
            }

            // What the answer is actually about (KON-362). plugin.json is a text file next to the code
            // it describes, so anything that can replace the code can leave the text saying what it
            // said — which made "com.acme.nerdctl@1.0.0" an answer about a name rather than about
            // anything that runs. A file that is there but unreadable hashes to nothing, which no
            // recorded answer matches: it stays a question rather than becoming a yes.
            var sha256 = Sha256OrEmpty(assembly);

            if (!isAllowed(new PluginCandidate(manifest, sha256)))
            {
                return new DiscoveredPlugin(directory, manifest, PluginStatus.AwaitingConsent, null, [])
                {
                    Sha256 = sha256,
                };
            }

            if (SdkTooOld(manifest.MinSdkVersion, out var sdkReason))
            {
                return new DiscoveredPlugin(directory, manifest, PluginStatus.Rejected, sdkReason, [])
                {
                    Sha256 = sha256,
                };
            }

            return Load(directory, manifest, assembly) with { Sha256 = sha256 };
        }
        catch (Exception ex)
        {
            // Containment is the point: one directory nobody vetted must not cost a start.
            return new DiscoveredPlugin(directory, null, PluginStatus.Rejected, ex.Message, []);
        }
    }

    /// <summary>
    /// The assembly's SHA-256 in lowercase hex, or empty when it cannot be read.
    /// <para>
    /// Every scan, not once at install: the same reasoning <see cref="Kontena.Sdk.Tooling.ManagedToolStore"/>
    /// already applies to the tools it downloads — a file verified when it arrived tells you nothing
    /// about the file you are about to run. A plugin assembly is a few hundred kilobytes, so this is
    /// microseconds against a scan that opens files and loads code anyway.
    /// </para>
    /// <para>
    /// Empty on failure rather than throwing: "we cannot say what these bytes are" and "these bytes are
    /// not what was agreed to" have the same answer, and only one of them is worth a separate path.
    /// </para>
    /// </summary>
    private static string Sha256OrEmpty(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(file));
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static DiscoveredPlugin Load(string directory, PluginManifest manifest, string assemblyPath)
    {
        var assembly = new PluginLoadContext(assemblyPath).LoadFromAssemblyPath(assemblyPath);

        var types = assembly.GetExportedTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false })
            .ToList();

        var engineType = types.Find(typeof(IEnginePlugin).IsAssignableFrom);
        var uiType = types.Find(typeof(IUiPlugin).IsAssignableFrom);

        DiscoveredPlugin Reject(string reason) =>
            new(directory, manifest, PluginStatus.Rejected, reason, []);

        if (engineType is null && uiType is null)
            return Reject("No IEnginePlugin or IUiPlugin in " + manifest.Assembly);

        var plugin = engineType is null ? null : Activator.CreateInstance(engineType) as IEnginePlugin;
        if (engineType is not null && plugin is null)
            return Reject(engineType.FullName + " is not an IEnginePlugin");

        // One instance when one type does both (KON-331). A plugin that contributes a backend and a
        // page is one plugin; constructing it twice would give it two of whatever it opened.
        var ui = ReferenceEquals(engineType, uiType)
            ? plugin as IUiPlugin
            : uiType is null ? null : Activator.CreateInstance(uiType) as IUiPlugin;
        if (uiType is not null && ui is null)
            return Reject(uiType.FullName + " is not an IUiPlugin");

        // The user agreed to what plugin.json said. Code that describes itself differently is not what
        // was agreed to. The digest recorded with the answer (KON-362) is what ties the answer to these
        // bytes; this ties those bytes to the name the user read in the dialog, which is a different
        // claim and still worth checking. Both entry points are asked: two types in one assembly can
        // disagree, and the one that would go unchecked is the one worth lying in.
        foreach (var declared in new[] { plugin?.Manifest, ui?.Manifest })
        {
            if (declared is null || (declared.Id == manifest.Id && declared.Version == manifest.Version))
                continue;

            return Reject(
                $"plugin.json says {manifest.Id} {manifest.Version}, the assembly says "
                + $"{declared.Id} {declared.Version}");
        }

        var providers = plugin?.GetProviders().ToList() ?? [];

        // Touch every identity member here, inside the containment this method already sits in. The
        // host reads these while building the very first switcher — outside any try, before there is a
        // window to report a failure in — so a getter that throws would take the launch down instead
        // of costing one plugin its place in the list.
        foreach (var provider in providers)
            _ = (provider.Backend, provider.DisplayName, provider.Chip, provider.Kind, provider.ChipStyle);

        // The pages, for the same reason. Not CreateView: building a control is the plugin's code
        // running in the shell's window, which the host does under its own containment at the moment
        // it navigates — here there is no UI thread yet, and no page to show a failure on.
        IReadOnlyList<PluginPage> pages = ui is null ? [] : [.. ui.GetPages()];

        return new DiscoveredPlugin(directory, manifest, PluginStatus.Loaded, null, providers)
        {
            Pages = pages,
        };
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
