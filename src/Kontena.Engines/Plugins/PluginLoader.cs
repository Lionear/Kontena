using System.Security.Cryptography;
using Kontena.Sdk;
using Kontena.Sdk.Tooling;

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
    public static string DefaultRoot { get; } = Path.Combine(ProductInfo.DataDirectory, "plugins");

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

            // Ahead of everything else, because it is answerable from plugin.json alone (KON-280) and
            // because the alternatives are worse in both directions: a consent prompt for a plugin that
            // cannot run here is a question with no useful answer, and hashing and loading an assembly
            // built for another operating system is work done to reach a failure already on the label.
            if (!PluginPlatform.SupportsHost(manifest.Platforms))
            {
                var wanted = string.Join(" or ", manifest.Platforms);
                return new DiscoveredPlugin(
                    directory, manifest, PluginStatus.Rejected, $"Not for this platform — needs {wanted}", []);
            }

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
        var assembly = new PluginLoadContext(assemblyPath).LoadWithoutLocking(assemblyPath);

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

        // What the plugin said it would contribute, held against what it did (KON-280). The point of
        // declaring it is that a wizard, a store listing and the switcher's grouping can be built
        // without instantiating a single provider — and a declaration nothing ever checks is precisely
        // the state MinSdkVersion sat in until it was enforced.
        if (manifest.ContributesUi != (ui is not null))
        {
            return Reject(manifest.ContributesUi
                ? "plugin.json declares a UI contribution, the assembly has no IUiPlugin"
                : $"{uiType!.FullName} contributes a UI that plugin.json does not declare");
        }

        if (manifest.Backends.Count > 0 && plugin is null)
        {
            return Reject(
                $"plugin.json declares backends ({string.Join(", ", manifest.Backends)}), "
                + "the assembly has no IEnginePlugin");
        }

        var providers = plugin?.GetProviders().ToList() ?? [];

        // Only one direction is a lie. A plugin that declares a kind and contributes none of it on this
        // machine is ordinary — nerdctl's providers are one per containerd namespace, and a machine
        // without nerdctl has none — so under-delivery passes. Contributing a kind that was never
        // declared does not: it is a backend appearing in a group the manifest said it would stay out of.
        var undeclared = providers.Select(p => p.Kind).Distinct().Except(manifest.Backends).ToList();
        if (undeclared.Count > 0)
        {
            return Reject(
                $"{engineType!.FullName} contributes {string.Join(", ", undeclared)} backends "
                + "that plugin.json does not declare");
        }

        // Same one-directional rule as the backends above, over what the plugin shells out to (KON-438).
        // The names in plugin.json are what the consent dialog showed, so a tool the assembly describes
        // and that file does not name is a tool the user never agreed to see run. Naming more than the
        // assembly ships stays fine: a plugin that dropped a tool between versions is not lying.
        //
        // Both entry points are asked and the result de-duplicated by name: a type that implements both
        // hands back two equal manifests, and two types can each bring their own tools.
        var tools = new[] { plugin?.Manifest, ui?.Manifest }
            .Where(m => m is not null)
            .SelectMany(m => m!.Tools)
            .DistinctBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        var unnamed = tools.Select(t => t.Name).Except(manifest.Tools, StringComparer.Ordinal).ToList();
        if (unnamed.Count > 0)
        {
            return Reject(
                $"The assembly drives {string.Join(", ", unnamed)}, which plugin.json does not list under tools");
        }

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
            Tools = tools,
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

        // major.minor.patch, and nothing else (KON-280). Version.TryParse alone also accepts "1.2" and
        // "1.2.3.4", which would leave the manifest saying two things — is the missing part a zero or a
        // wildcard? — about the one number that decides whether foreign code runs. Build >= 0 rules out
        // the two-part form, Revision == -1 the four-part one. A prerelease suffix never parses at all,
        // which is the right answer: the SDK assembly version it is compared against has no such part.
        if (!Version.TryParse(minSdkVersion, out var required) || required is not { Build: >= 0, Revision: -1 })
        {
            reason = $"MinSdkVersion '{minSdkVersion}' is not a major.minor.patch version";
            return true;
        }

        var host = typeof(IEnginePlugin).Assembly.GetName().Version;
        if (host is not null && required <= host)
            return false;

        reason = $"Needs Kontena.Sdk {minSdkVersion}, this build has {host?.ToString() ?? "none"}";
        return true;
    }
}
