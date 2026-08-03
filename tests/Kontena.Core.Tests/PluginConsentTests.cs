using Kontena.Core.Models;

namespace Kontena.Core.Tests;

/// <summary>
/// Consent is recorded per id *and* version: an update is different bytes, and the permission was
/// given for the old ones. Until signing (KON-53) that is the only honest boundary there is.
/// </summary>
public sealed class PluginConsentTests
{
    [Fact]
    public void A_fresh_settings_file_allows_nothing()
    {
        Assert.False(new KontenaSettings().AllowsPlugin("com.acme.nerdctl", "1.0.0"));
    }

    [Fact]
    public void An_allowed_plugin_is_allowed()
    {
        var settings = new KontenaSettings().WithAllowedPlugin("com.acme.nerdctl", "1.0.0");

        Assert.True(settings.AllowsPlugin("com.acme.nerdctl", "1.0.0"));
    }

    [Fact]
    public void A_new_version_of_an_allowed_plugin_is_not_allowed()
    {
        var settings = new KontenaSettings().WithAllowedPlugin("com.acme.nerdctl", "1.0.0");

        Assert.False(settings.AllowsPlugin("com.acme.nerdctl", "1.1.0"));
    }

    [Fact]
    public void Allowing_the_same_plugin_twice_records_it_once()
    {
        var settings = new KontenaSettings()
            .WithAllowedPlugin("com.acme.nerdctl", "1.0.0")
            .WithAllowedPlugin("com.acme.nerdctl", "1.0.0");

        Assert.Single(settings.AllowedPlugins);
    }

    [Fact]
    public void Allowing_a_new_version_keeps_the_old_entry()
    {
        var settings = new KontenaSettings()
            .WithAllowedPlugin("com.acme.nerdctl", "1.0.0")
            .WithAllowedPlugin("com.acme.nerdctl", "1.1.0");

        Assert.Equal(2, settings.AllowedPlugins.Count);
        Assert.True(settings.AllowsPlugin("com.acme.nerdctl", "1.0.0"));
    }
}
