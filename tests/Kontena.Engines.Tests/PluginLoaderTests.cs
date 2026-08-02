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

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
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
}
