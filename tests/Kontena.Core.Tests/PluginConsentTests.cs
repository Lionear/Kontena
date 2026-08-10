using Kontena.Core.Models;

namespace Kontena.Core.Tests;

/// <summary>
/// Consent is recorded against the assembly's digest, under the id and version a person recognises it
/// by (KON-362). Until signing (KON-53) that is the only honest boundary there is — and it has to be
/// about bytes, because <c>plugin.json</c> is a text file beside the code it describes.
/// </summary>
public sealed class PluginConsentTests
{
    private const string Sha = "9f2c0d1e4b7a63f58c21d0ae3b94f6172d8e05ca7b3149ef62a80d5c73b1e4a9";
    private const string OtherSha = "1a1a1a1a2b2b2b2b3c3c3c3c4d4d4d4d5e5e5e5e6f6f6f6f7070707081818181";

    [Fact]
    public void A_fresh_settings_file_allows_nothing()
    {
        Assert.False(new KontenaSettings().AllowsPlugin("com.acme.nerdctl", "1.0.0", Sha));
    }

    [Fact]
    public void An_allowed_plugin_is_allowed()
    {
        var settings = new KontenaSettings().WithAllowedPlugin("com.acme.nerdctl", "1.0.0", Sha);

        Assert.True(settings.AllowsPlugin("com.acme.nerdctl", "1.0.0", Sha));
    }

    [Fact]
    public void A_new_version_of_an_allowed_plugin_is_not_allowed()
    {
        var settings = new KontenaSettings().WithAllowedPlugin("com.acme.nerdctl", "1.0.0", Sha);

        Assert.False(settings.AllowsPlugin("com.acme.nerdctl", "1.1.0", Sha));
    }

    [Fact]
    public void The_same_version_carrying_different_code_is_not_allowed()
    {
        // The hole this closes: an answer given for one dll used to cover any dll that kept the same
        // id and version, which is a claim about a name rather than about anything that runs.
        var settings = new KontenaSettings().WithAllowedPlugin("com.acme.nerdctl", "1.0.0", Sha);

        Assert.False(settings.AllowsPlugin("com.acme.nerdctl", "1.0.0", OtherSha));
    }

    [Fact]
    public void An_unreadable_assembly_is_never_allowed()
    {
        // Sha256OrEmpty answers with an empty string when it cannot read the file. "We cannot say what
        // these bytes are" must not become "these are the bytes you agreed to", not even if an empty
        // digest somehow ended up recorded.
        var settings = new KontenaSettings { AllowedPlugins = ["com.acme.nerdctl@1.0.0#"] };

        Assert.False(settings.AllowsPlugin("com.acme.nerdctl", "1.0.0", string.Empty));
    }

    [Fact]
    public void Allowing_the_same_plugin_twice_records_it_once()
    {
        var settings = new KontenaSettings()
            .WithAllowedPlugin("com.acme.nerdctl", "1.0.0", Sha)
            .WithAllowedPlugin("com.acme.nerdctl", "1.0.0", Sha);

        Assert.Single(settings.AllowedPlugins);
    }

    [Fact]
    public void Allowing_a_new_version_keeps_the_old_entry()
    {
        var settings = new KontenaSettings()
            .WithAllowedPlugin("com.acme.nerdctl", "1.0.0", Sha)
            .WithAllowedPlugin("com.acme.nerdctl", "1.1.0", Sha);

        Assert.Equal(2, settings.AllowedPlugins.Count);
        Assert.True(settings.AllowsPlugin("com.acme.nerdctl", "1.0.0", Sha));
    }

    [Fact]
    public void An_entry_from_before_the_digest_matches_nothing()
    {
        // Settings written by an older build carry no '#'. There is no record of which bytes were
        // agreed to, so the only honest answer is to ask again rather than to assume.
        var settings = new KontenaSettings { AllowedPlugins = ["com.acme.nerdctl@1.0.0"] };

        Assert.False(settings.AllowsPlugin("com.acme.nerdctl", "1.0.0", Sha));
        Assert.False(settings.KnowsPlugin("com.acme.nerdctl", "1.0.0"));
    }

    [Fact]
    public void A_plugin_that_changed_is_known_even_though_it_is_not_allowed()
    {
        // What the prompt reads from to tell "something new appeared" apart from "what you allowed is
        // not what is there now" — two different things to say to a user.
        var settings = new KontenaSettings().WithAllowedPlugin("com.acme.nerdctl", "1.0.0", Sha);

        Assert.False(settings.AllowsPlugin("com.acme.nerdctl", "1.0.0", OtherSha));
        Assert.True(settings.KnowsPlugin("com.acme.nerdctl", "1.0.0"));
        Assert.False(settings.KnowsPlugin("com.acme.nerdctl", "2.0.0"));
    }
}
