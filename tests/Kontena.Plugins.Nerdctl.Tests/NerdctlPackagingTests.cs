using System.Text.Json;
using Kontena.Sdk;
using Kontena.Sdk.Tooling.Fakes;

namespace Kontena.Plugins.Nerdctl.Tests;

/// <summary>
/// The two things that decide whether a packaged plugin is loadable at all (KON-141 PR 5), neither of
/// which any other test would notice: the host instantiates the entry type with no arguments, and it
/// rejects the plugin outright when <c>plugin.json</c> and the assembly disagree about what they are.
/// <para>
/// Both failures happen only after the zip has been built, downloaded, unpacked and consented to —
/// there is no compiler error and no failing call, just a backend that never appears. That is what
/// these tests are for.
/// </para>
/// </summary>
public sealed class NerdctlPackagingTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// The manifest as it travels next to the dll. Read from the build output rather than from the
    /// source tree, because the output copy is the one that ends up in the zip — a plugin.json that
    /// exists in the repo but never got copied would still fail on a user's machine.
    /// </summary>
    private sealed record PackagedManifest(
        string Id, string Name, string Version, string Author, string Description,
        string MinSdkVersion, string Assembly);

    private static PackagedManifest Manifest()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "plugin.json");
        Assert.True(File.Exists(path), $"plugin.json is not in the build output ({path}).");

        return JsonSerializer.Deserialize<PackagedManifest>(File.ReadAllText(path), Options)!;
    }

    [Fact]
    public void The_entry_type_can_be_constructed_the_way_the_loader_constructs_it()
    {
        // PluginLoader calls Activator.CreateInstance(entryType) — no arguments, no host services.
        // A plugin whose only constructor takes an IToolRunner is discovered and then rejected with a
        // MissingMethodException, which reads as "this plugin is broken" rather than "it wanted
        // something".
        var plugin = Activator.CreateInstance(typeof(NerdctlPlugin));

        Assert.IsAssignableFrom<IEnginePlugin>(plugin);
    }

    [Fact]
    public void The_packaged_manifest_says_exactly_what_the_assembly_says()
    {
        // The loader compares these two after consent and rejects the plugin when id or version
        // differ — the only thing tying "what the user agreed to" to "what the code claims" until
        // signing lands. Every field is compared anyway: the rest is what the consent prompt shows.
        // Constructed through the runner overload on purpose: this test is about what the two
        // manifests say, so it must keep compiling — and keep failing at run time — if the
        // parameterless constructor the loader needs ever disappears.
        var declared = new NerdctlPlugin(new FakeToolRunner()).Manifest;
        var packaged = Manifest();

        Assert.Equal(declared.Id, packaged.Id);
        Assert.Equal(declared.Version, packaged.Version);
        Assert.Equal(declared.Name, packaged.Name);
        Assert.Equal(declared.Author, packaged.Author);
        Assert.Equal(declared.Description, packaged.Description);
        Assert.Equal(declared.MinSdkVersion, packaged.MinSdkVersion);
    }

    [Fact]
    public void The_manifest_names_the_assembly_that_is_actually_built()
    {
        // The loader resolves this name inside the plugin directory and rejects the plugin if no such
        // file is there. Renaming the project without renaming this is a release nobody can load.
        var expected = Path.GetFileName(typeof(NerdctlPlugin).Assembly.Location);

        Assert.Equal(expected, Manifest().Assembly);
    }

    [Fact]
    public void The_packaged_layout_loads_through_the_hosts_own_loader()
    {
        // The end of the chain the other tests only check pieces of: a directory holding exactly what
        // the release zip holds, read by the same PluginLoader the app runs at start. It exercises
        // what nothing else here can — that the load context binds the plugin to the host's
        // Kontena.Sdk, that an IEnginePlugin is found in the assembly, and that GetProviders survives
        // being called on a machine with no nerdctl (this one), which the loader does eagerly.
        var root = Directory.CreateTempSubdirectory("kontena-nerdctl-plugin-root").FullName;
        var directory = Directory.CreateDirectory(Path.Combine(root, "nerdctl")).FullName;

        // Same three files the packaging step ships, from the build output — deps.json included where
        // it exists, since that is what AssemblyDependencyResolver reads.
        foreach (var name in (string[])["Kontena.Plugins.Nerdctl.dll", "Kontena.Plugins.Nerdctl.deps.json", "plugin.json"])
        {
            var source = Path.Combine(AppContext.BaseDirectory, name);
            if (File.Exists(source))
                File.Copy(source, Path.Combine(directory, name), overwrite: true);
        }

        var discovered = Kontena.Engines.Plugins.PluginLoader.Discover(root, _ => true);

        var only = Assert.Single(discovered);
        Assert.True(
            only.Status == Kontena.Engines.Plugins.PluginStatus.Loaded,
            $"The packaged plugin was not loaded: {only.Reason}");
        Assert.Equal("com.kontena.nerdctl", only.Manifest!.Id);
        // One provider per containerd namespace, and one on "default" when nerdctl cannot be asked at
        // all — never zero, or the backend would vanish from the switcher instead of saying it is not
        // connected.
        Assert.NotEmpty(only.Providers);

        Directory.Delete(root, recursive: true);
    }
}
