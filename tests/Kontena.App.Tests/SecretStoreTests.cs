using Kontena.App.Services;
using Kontena.Core.Orchestration;

namespace Kontena.App.Tests;

/// <summary>
/// The key naming, which is the part the user sees: these strings appear in Seahorse, KWallet and
/// Keychain Access, where someone has to recognise and be able to revoke them.
/// </summary>
public class SecretKeyTests
{
    [Fact]
    public void Every_key_is_recognisably_ours() =>
        Assert.All(
            new[] { SecretKeys.Registry("ghcr.io"), SecretKeys.Engine("ssh://build-01") },
            key => Assert.StartsWith(SecretKeys.Prefix + ":", key, StringComparison.Ordinal));

    [Theory]
    [InlineData("ghcr.io", "kontena:registry:ghcr.io")]
    [InlineData("GHCR.io", "kontena:registry:ghcr.io")]
    [InlineData("  ghcr.io  ", "kontena:registry:ghcr.io")]
    public void A_host_is_one_entry_however_it_was_typed(string host, string expected) =>
        // Two entries for the same registry would shadow each other, and the one that wins would depend
        // on how it was typed the second time.
        Assert.Equal(expected, SecretKeys.Registry(host));

    [Fact]
    public void Registries_and_engines_never_collide() =>
        Assert.NotEqual(SecretKeys.Registry("example.com"), SecretKeys.Engine("example.com"));

    [Theory]
    [InlineData("kontena:registry:ghcr.io", "Kontena — registry login for ghcr.io")]
    [InlineData("kontena:engine:ssh://build-01", "Kontena — engine credentials for ssh://build-01")]
    // A registry on a port is an ordinary thing, and its colon used to split the label apart.
    [InlineData("kontena:registry:localhost:5000", "Kontena — registry login for localhost:5000")]
    public void The_keychain_label_says_what_the_entry_is(string key, string expected) =>
        Assert.Equal(expected, SecretKeys.Describe(key));
}

/// <summary>
/// The store against the machine's real keychain. Skips when there is none — a build agent has no
/// Secret Service, and that is exactly the case <see cref="ISecretStore.IsAvailable"/> exists for.
/// </summary>
public class SecretStoreTests : IAsyncLifetime
{
    private readonly ISecretStore _store = SecretStore.Create();
    private readonly string _key = $"{SecretKeys.Prefix}:test:{Guid.NewGuid():N}";

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Leaves nothing behind in the user's own keychain, whatever the test did.</summary>
    public async Task DisposeAsync()
    {
        if (_store.IsAvailable)
            await _store.DeleteAsync(_key);
    }

    [SkippableFact]
    public async Task Stores_reads_back_and_removes_a_secret()
    {
        Skip.If(!_store.IsAvailable, "No OS keychain on this session.");

        Assert.True(await _store.SetAsync(_key, "hunter2"));
        Assert.Equal("hunter2", await _store.GetAsync(_key));

        await _store.DeleteAsync(_key);

        // Gone means gone: a stale secret surviving a delete is how a revoked login keeps working.
        Assert.Null(await _store.GetAsync(_key));
    }

    [SkippableFact]
    public async Task Overwrites_rather_than_keeping_both()
    {
        Skip.If(!_store.IsAvailable, "No OS keychain on this session.");

        await _store.SetAsync(_key, "first");
        await _store.SetAsync(_key, "second");

        Assert.Equal("second", await _store.GetAsync(_key));
    }

    [SkippableFact]
    public async Task Reading_something_that_was_never_stored_is_null_not_an_error()
    {
        Skip.If(!_store.IsAvailable, "No OS keychain on this session.");

        Assert.Null(await _store.GetAsync($"{SecretKeys.Prefix}:test:{Guid.NewGuid():N}"));
    }

    [SkippableFact]
    public async Task Deleting_something_that_is_not_there_is_harmless()
    {
        Skip.If(!_store.IsAvailable, "No OS keychain on this session.");

        await _store.DeleteAsync($"{SecretKeys.Prefix}:test:{Guid.NewGuid():N}");
    }

    [SkippableFact]
    public async Task Handles_a_secret_that_is_not_ascii()
    {
        // The marshalling is UTF-8 because glib takes UTF-8; a token with a non-ASCII character in it
        // would come back mangled if that were wrong, and it would be wrong silently.
        Skip.If(!_store.IsAvailable, "No OS keychain on this session.");

        const string secret = "pässwörd–π";
        await _store.SetAsync(_key, secret);

        Assert.Equal(secret, await _store.GetAsync(_key));
    }

    [Fact]
    public async Task An_unavailable_store_stores_nothing_and_says_so()
    {
        var store = new UnavailableSecretStore();

        Assert.False(store.IsAvailable);
        Assert.False(await store.SetAsync("k", "v"));
        Assert.Null(await store.GetAsync("k"));
    }
}
