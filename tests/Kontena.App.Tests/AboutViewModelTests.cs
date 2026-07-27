using Kontena.App.Services;
using Kontena.App.ViewModels;

namespace Kontena.App.Tests;

/// <summary>
/// About as its own screen (KON-135).
/// <para>
/// Two things are worth holding: the keychain note must follow the session it is describing rather
/// than repeat a promise, and the Activity quick action must not be offered when there is nothing to
/// navigate with — a row that leads nowhere is worse than a missing one (KON-117).
/// </para>
/// </summary>
public sealed class AboutViewModelTests
{
    private sealed class AvailableStore : ISecretStore
    {
        public bool IsAvailable => true;

        public ValueTask<bool> SetAsync(string key, string secret, CancellationToken ct = default) =>
            ValueTask.FromResult(true);

        public ValueTask<string?> GetAsync(string key, CancellationToken ct = default) =>
            ValueTask.FromResult<string?>(null);

        public ValueTask DeleteAsync(string key, CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    [Fact]
    public void The_keychain_note_says_where_credentials_go_when_there_is_a_keychain()
    {
        var page = new AboutViewModel(new AvailableStore());

        Assert.Contains("system keychain", page.KeychainStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("cannot store", page.KeychainStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void The_keychain_note_admits_it_when_there_is_none()
    {
        var page = new AboutViewModel(new UnavailableSecretStore());

        Assert.Contains("cannot store credentials", page.KeychainStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void The_activity_action_is_hidden_when_the_shell_cannot_navigate()
    {
        var page = new AboutViewModel(new UnavailableSecretStore());

        Assert.False(page.HasActivity);
    }

    [Fact]
    public void The_activity_action_asks_the_shell_to_navigate()
    {
        var asked = 0;
        var page = new AboutViewModel(new UnavailableSecretStore(), () => asked++);

        Assert.True(page.HasActivity);
        page.ShowActivityCommand.Execute(null);

        Assert.Equal(1, asked);
    }
}
