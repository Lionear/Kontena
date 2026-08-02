using System.Text.Json;
using Kontena.Engines.Plugins;

namespace Kontena.Engines.Tests;

/// <summary>
/// The loader is the one place that turns files on disk into running code, so its failure modes are
/// the tests: nothing loads without consent, nothing loads that claims a newer SDK, and nothing that
/// goes wrong in one directory reaches the caller.
/// </summary>
public sealed class PluginLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kontena-plugin-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// The fixture assembly, built by <c>tests/Kontena.TestPlugin</c> into this project's output. It
    /// carries its own copy of Kontena.Sdk.dll, which is the whole point: the loader must ignore it.
    /// </summary>
    private static string FixtureDirectory =>
        Path.Combine(AppContext.BaseDirectory, "plugin-fixture");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>Copy the built fixture into the plugins root and give it a manifest.</summary>
    private string InstallFixture(string version = "1.0.0", string minSdk = "0.1.0", string? id = null)
    {
        id ??= "com.kontena.test";
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);

        foreach (var file in Directory.GetFiles(FixtureDirectory))
            File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);

        File.WriteAllText(Path.Combine(dir, "plugin.json"), JsonSerializer.Serialize(new
        {
            id,
            name = "Test Plugin",
            version,
            author = "Kontena",
            description = "A plugin.",
            minSdkVersion = minSdk,
            assembly = "Kontena.TestPlugin.dll",
        }));

        return dir;
    }

    /// <summary>Write a plugin directory containing only a manifest — no assembly.</summary>
    private string WriteManifest(
        string id, string version = "1.0.0", string minSdk = "0.1.0", string assembly = "Nothing.dll")
    {
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), JsonSerializer.Serialize(new
        {
            id,
            name = "Test",
            version,
            author = "Kontena",
            description = "A plugin.",
            minSdkVersion = minSdk,
            assembly,
        }));
        return dir;
    }

    [Fact]
    public void A_missing_root_yields_nothing()
    {
        Assert.Empty(PluginLoader.Discover(Path.Combine(_root, "does-not-exist"), _ => true));
    }

    [Fact]
    public void An_empty_directory_is_rejected_with_a_reason()
    {
        Directory.CreateDirectory(Path.Combine(_root, "empty"));

        var found = Assert.Single(PluginLoader.Discover(_root, _ => true));

        Assert.Equal(PluginStatus.Rejected, found.Status);
        Assert.NotNull(found.Reason);
    }

    [Fact]
    public void An_unreadable_manifest_is_rejected_with_a_reason()
    {
        var dir = Path.Combine(_root, "broken");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), "{ this is not json");

        var found = Assert.Single(PluginLoader.Discover(_root, _ => true));

        Assert.Equal(PluginStatus.Rejected, found.Status);
        Assert.NotNull(found.Reason);
    }

    [Fact]
    public void A_manifest_missing_a_required_field_is_rejected_with_a_reason()
    {
        var dir = Path.Combine(_root, "incomplete");
        Directory.CreateDirectory(dir);
        // Valid JSON, but no id and no assembly: System.Text.Json throws on the required members, and
        // that has to land as a rejection like any other unreadable manifest.
        File.WriteAllText(Path.Combine(dir, "plugin.json"), """{ "name": "Half a plugin" }""");

        var found = Assert.Single(PluginLoader.Discover(_root, _ => true));

        Assert.Equal(PluginStatus.Rejected, found.Status);
        Assert.NotNull(found.Reason);
    }

    [Fact]
    public void A_plugin_without_consent_awaits_it_and_is_not_loaded()
    {
        WriteManifest("com.kontena.test");

        var found = Assert.Single(PluginLoader.Discover(_root, _ => false));

        Assert.Equal(PluginStatus.AwaitingConsent, found.Status);
        Assert.Equal("com.kontena.test", found.Manifest!.Id);
        Assert.Empty(found.Providers);
    }

    [Fact]
    public void Consent_is_asked_per_id_and_version()
    {
        WriteManifest("com.kontena.test", version: "2.0.0");

        var found = Assert.Single(PluginLoader.Discover(
            _root, m => m.Id == "com.kontena.test" && m.Version == "1.0.0"));

        Assert.Equal(PluginStatus.AwaitingConsent, found.Status);
    }

    [Fact]
    public void A_missing_assembly_is_rejected_rather_than_thrown()
    {
        WriteManifest("com.kontena.test", assembly: "NotThere.dll");

        var found = Assert.Single(PluginLoader.Discover(_root, _ => true));

        Assert.Equal(PluginStatus.Rejected, found.Status);
        Assert.NotNull(found.Reason);
    }

    [Fact]
    public void One_broken_directory_does_not_hide_a_healthy_one()
    {
        Directory.CreateDirectory(Path.Combine(_root, "empty"));
        WriteManifest("com.kontena.test");

        var found = PluginLoader.Discover(_root, _ => false);

        Assert.Equal(2, found.Count);
        Assert.Contains(found, p => p.Status == PluginStatus.AwaitingConsent);
        Assert.Contains(found, p => p.Status == PluginStatus.Rejected);
    }

    [Fact]
    public void The_fixture_ships_its_own_sdk_copy()
    {
        // Guards the guard: if this stops being true, the test below proves nothing.
        Assert.True(File.Exists(Path.Combine(FixtureDirectory, "Kontena.Sdk.dll")));
    }

    [Fact]
    public void An_allowed_plugin_loads_and_contributes_its_providers()
    {
        InstallFixture();

        var found = Assert.Single(PluginLoader.Discover(_root, _ => true));

        Assert.Equal(PluginStatus.Loaded, found.Status);
        Assert.Null(found.Reason);
        var provider = Assert.Single(found.Providers);
        Assert.Equal("testplugin", provider.Backend);
    }

    [Fact]
    public void The_sdk_comes_from_the_host_even_though_the_plugin_ships_one()
    {
        InstallFixture();

        var found = Assert.Single(PluginLoader.Discover(_root, _ => true));
        var provider = Assert.Single(found.Providers);

        // The cast in the loader already proves type identity, but say so out loud: this is the
        // failure that produces no exception and no backend, only silence.
        Assert.Same(
            typeof(Kontena.Sdk.IBackendProvider),
            provider.GetType().GetInterface("Kontena.Sdk.IBackendProvider"));
    }

    [Fact]
    public void A_plugin_that_contributes_nothing_useful_is_rejected_rather_than_thrown()
    {
        // A directory whose assembly holds no IEnginePlugin: point the manifest at the SDK itself.
        var dir = InstallFixture();
        File.WriteAllText(Path.Combine(dir, "plugin.json"), JsonSerializer.Serialize(new
        {
            id = "com.kontena.test",
            name = "Test Plugin",
            version = "1.0.0",
            author = "Kontena",
            description = "A plugin.",
            minSdkVersion = "0.1.0",
            assembly = "Kontena.Sdk.dll",
        }));

        var found = Assert.Single(PluginLoader.Discover(_root, _ => true));

        Assert.Equal(PluginStatus.Rejected, found.Status);
        Assert.NotNull(found.Reason);
    }

    [Fact]
    public void A_plugin_that_needs_a_newer_sdk_is_rejected_with_a_reason()
    {
        InstallFixture(minSdk: "99.0.0");

        var found = Assert.Single(PluginLoader.Discover(_root, _ => true));

        Assert.Equal(PluginStatus.Rejected, found.Status);
        Assert.Contains("99.0.0", found.Reason);
        Assert.Empty(found.Providers);
    }

    [Fact]
    public void A_plugin_that_needs_exactly_this_sdk_loads()
    {
        var host = typeof(Kontena.Sdk.IEnginePlugin).Assembly.GetName().Version!;
        InstallFixture(minSdk: $"{host.Major}.{host.Minor}.{host.Build}");

        var found = Assert.Single(PluginLoader.Discover(_root, _ => true));

        Assert.Equal(PluginStatus.Loaded, found.Status);
    }

    [Fact]
    public void A_plugin_without_a_minimum_sdk_loads()
    {
        InstallFixture(minSdk: "");

        var found = Assert.Single(PluginLoader.Discover(_root, _ => true));

        Assert.Equal(PluginStatus.Loaded, found.Status);
    }

    [Fact]
    public void An_unparseable_minimum_sdk_is_rejected_rather_than_ignored()
    {
        InstallFixture(minSdk: "banana");

        var found = Assert.Single(PluginLoader.Discover(_root, _ => true));

        Assert.Equal(PluginStatus.Rejected, found.Status);
        Assert.NotNull(found.Reason);
    }

    [Fact]
    public void A_plugin_whose_code_disagrees_with_its_manifest_is_rejected()
    {
        // The fixture's own manifest says 1.0.0; the file on disk claims 9.9.9. Consent was given for
        // what the file said, so the code may not turn out to be something else.
        InstallFixture(version: "9.9.9");

        var found = Assert.Single(PluginLoader.Discover(_root, _ => true));

        Assert.Equal(PluginStatus.Rejected, found.Status);
        Assert.NotNull(found.Reason);
    }
}
