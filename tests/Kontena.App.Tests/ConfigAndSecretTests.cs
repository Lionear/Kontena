using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// ConfigMaps and Secrets (KON-249). Both were already browsable through the generic resource
/// browser — as raw YAML, which for a Secret means base64: unreadable and fully exposed at the same
/// time. What is worth pinning is the pairing these pages replace it with: a value is never on
/// screen unless it was asked for, and it does not stay there once it is hidden.
/// </summary>
public sealed class ConfigAndSecretTests
{
    private static ClusterSecretsViewModel Secrets() => new(new FakeClusterEngine(), "app");
    private static ClusterConfigMapsViewModel ConfigMaps() => new(new FakeClusterEngine(), "app");

    private static async Task<ConfigObjectRow> SecretAsync(string name)
    {
        var page = Secrets();
        await page.LoadAsync();
        return page.Items.Single(r => r.Name == name);
    }

    [Fact]
    public async Task Listing_secrets_carries_keys_and_sizes_and_no_values()
    {
        // The whole design rests on this: a page showing fifty secrets holds none of their values.
        var row = await SecretAsync("postgres-credentials");

        Assert.Equal("2 keys", row.KeyCount);
        Assert.All(row.Keys, key => Assert.Null(key.Value));
        Assert.All(row.Keys, key => Assert.False(key.IsRevealed));

        // The size is worth knowing and gives nothing away — a 24-byte key is a password and a
        // 1.7 kB one is a certificate.
        Assert.Equal("24 B", row.Keys.Single(k => k.Name == "password").Size);
    }

    [Fact]
    public async Task A_secret_value_arrives_only_when_it_is_asked_for()
    {
        var row = await SecretAsync("postgres-credentials");
        var key = row.Keys.Single(k => k.Name == "password");

        Assert.Null(key.Value);

        await key.ToggleCommand.ExecuteAsync(null);

        Assert.True(key.IsRevealed);
        Assert.Equal("s3cr3t-but-not-really", key.Value);
    }

    [Fact]
    public async Task Hiding_a_value_drops_it_rather_than_folding_it_away()
    {
        // A cache would leave a secret in this process for as long as the page is open, having been
        // shown once. That is the state these pages exist to avoid, so Hide clears and the next Show
        // asks the cluster again.
        var row = await SecretAsync("postgres-credentials");
        var key = row.Keys.Single(k => k.Name == "password");

        await key.ToggleCommand.ExecuteAsync(null);
        await key.ToggleCommand.ExecuteAsync(null);

        Assert.False(key.IsRevealed);
        Assert.Null(key.Value);
    }

    [Fact]
    public async Task Bytes_that_are_not_text_are_never_rendered_as_text()
    {
        // Half a TLS key drawn as characters is noise, it can put a terminal into a state nobody
        // asked for, and it is not what the value is.
        var row = await SecretAsync("app-tls");
        var key = row.Keys.Single(k => k.Name == "tls.key");

        await key.ToggleCommand.ExecuteAsync(null);

        Assert.True(key.IsRevealed);
        Assert.True(key.IsBinary);
        Assert.Null(key.Value);
        Assert.Contains("base64", key.BinaryNotice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copying_never_puts_the_value_on_screen()
    {
        // Copy and reveal are separate acts: a password goes into a terminal far more often than
        // onto a screen someone else can see.
        var row = await SecretAsync("postgres-credentials");
        var key = row.Keys.Single(k => k.Name == "password");

        var copied = await key.ForClipboardAsync();

        Assert.Equal("s3cr3t-but-not-really", copied);
        Assert.False(key.IsRevealed);
        Assert.Null(key.Value);
    }

    [Fact]
    public async Task A_config_map_has_nothing_to_hide_and_does_not_pretend_otherwise()
    {
        // Making someone press Show on a LOG_LEVEL of "info" teaches them to press it without
        // reading, which is the habit the secrets page depends on them not having.
        var page = ConfigMaps();
        await page.LoadAsync();

        var row = page.Items.Single(r => r.Name == "web-config");
        var key = row.Keys.Single(k => k.Name == "LOG_LEVEL");

        // The value is fetched in the row's constructor for ConfigMaps, so give it a moment.
        for (var i = 0; i < 50 && !key.IsRevealed; i++)
            await Task.Delay(5);

        Assert.False(key.IsSecret);
        Assert.True(key.IsRevealed);
        Assert.Equal("info", key.Value);
    }

    [Fact]
    public async Task The_secrets_page_finds_an_object_by_a_key_it_holds()
    {
        // "Which secret holds tls.key" is a question the object name cannot answer, and it is the
        // one you actually have.
        var page = Secrets();
        await page.LoadAsync();

        page.SearchText = "tls.key";

        Assert.Equal("app-tls", Assert.Single(page.Items).Name);
    }

    [Fact]
    public async Task A_type_the_cluster_did_not_give_reads_as_a_dash_rather_than_as_empty()
    {
        var page = Secrets();
        await page.LoadAsync();

        Assert.All(page.Items, row => Assert.NotEqual(string.Empty, row.Type));
        Assert.Equal("kubernetes.io/tls", page.Items.Single(r => r.Name == "app-tls").Type);
    }

    [Fact]
    public void Asking_a_third_kind_for_configuration_data_is_a_bug_and_says_so()
    {
        // An empty list would look like an object with no keys, which is a different and valid
        // answer.
        var engine = new FakeClusterEngine();
        var pod = new ResourceRef(GroupVersionKind.Pod, "app", "web-7f9");

        Assert.Throws<NotSupportedException>(() => engine.GetConfigDataAsync(pod));
    }
}
