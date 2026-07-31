using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.Core.Models;
using Kontena.Sdk.Models;
using Xunit;

namespace Kontena.App.Tests;

/// <summary>
/// Choosing a credential in the remote-engine form (KON-261, KON-259).
/// <para>
/// The rules that matter here are about what must <i>not</i> happen: a password reaching the settings
/// file, an option offered where there is nowhere to store its secret, and an engine saved whose
/// password was never actually written.
/// </para>
/// </summary>
public sealed class SshCredentialFormTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"kontena-credentials-{Guid.NewGuid():N}.json");

    private readonly string _keyFile = Path.Combine(
        Path.GetTempPath(), $"kontena-key-{Guid.NewGuid():N}");

    public SshCredentialFormTests() => File.WriteAllText(_keyFile, "not really a key");

    public void Dispose()
    {
        foreach (var path in new[] { _path, _keyFile })
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private sealed class RecordingSecrets : ISecretStore
    {
        public RecordingSecrets(bool available = true, bool accepts = true)
        {
            IsAvailable = available;
            Accepts = accepts;
        }

        public bool IsAvailable { get; }
        public bool Accepts { get; }
        public Dictionary<string, string> Stored { get; } = [];
        public List<string> Deleted { get; } = [];

        public ValueTask<bool> SetAsync(string key, string secret, CancellationToken ct = default)
        {
            if (!Accepts)
                return ValueTask.FromResult(false);

            Stored[key] = secret;
            return ValueTask.FromResult(true);
        }

        public ValueTask<string?> GetAsync(string key, CancellationToken ct = default) =>
            ValueTask.FromResult(Stored.TryGetValue(key, out var value) ? value : null);

        public ValueTask DeleteAsync(string key, CancellationToken ct = default)
        {
            Deleted.Add(key);
            Stored.Remove(key);
            return ValueTask.CompletedTask;
        }
    }

    private SettingsViewModel Form(ISecretStore secrets)
    {
        var store = new SettingsStore(_path);
        var settings = new KontenaSettings();
        store.Save(settings);

        return new SettingsViewModel(
            store, settings, [], autostart: new UnsupportedAutostart(), secrets: secrets)
        {
            RemoteName = "Build server",
            RemoteHost = "build-01",
        };
    }

    private KontenaSettings Saved() => new SettingsStore(_path).Load();

    // ── The key file (KON-261) ────────────────────────────────────────────────

    [Fact]
    public async Task A_key_file_is_carried_into_the_engine()
    {
        var form = Form(new RecordingSecrets());
        form.RemoteKeyFile = _keyFile;

        Assert.True(form.CanAddRemote);
        await form.AddRemoteCommand.ExecuteAsync(null);

        Assert.Equal(_keyFile, Assert.Single(Saved().RemoteEngines).KeyFile);
    }

    [Fact]
    public void A_key_file_that_is_not_there_is_refused_before_connecting()
    {
        // ssh reports a missing key as "Permission denied (publickey)" — a message about the host,
        // for a problem on this machine's own disk.
        var form = Form(new RecordingSecrets());
        form.RemoteKeyFile = "/home/rick/.ssh/typo_ed25519";

        Assert.False(form.CanAddRemote);
    }

    [Fact]
    public void The_public_half_is_recognised_and_refused()
    {
        // The mistake a file picker makes easiest: both halves sit next to each other in ~/.ssh and
        // only one is the identity. ssh calls it a rejected key, which reads as a problem on the host.
        var publicHalf = _keyFile + ".pub";
        File.WriteAllText(publicHalf, "ssh-ed25519 AAAA");

        try
        {
            var form = Form(new RecordingSecrets());
            form.RemoteKeyFile = publicHalf;

            Assert.False(form.CanAddRemote);
        }
        finally
        {
            File.Delete(publicHalf);
        }
    }

    [Fact]
    public async Task Switching_to_TCP_leaves_the_key_behind()
    {
        // Same rule the socket path already follows: a value typed under one transport must not come
        // back under the other.
        var form = Form(new RecordingSecrets());
        form.RemoteKeyFile = _keyFile;
        form.RemoteIsSsh = false;
        form.RemoteAllowInsecure = true;

        await form.AddRemoteCommand.ExecuteAsync(null);

        Assert.Null(Assert.Single(Saved().RemoteEngines).KeyFile);
    }

    // ── The password (KON-259) ────────────────────────────────────────────────

    [Fact]
    public void Without_a_keychain_the_password_option_is_not_offered()
    {
        // There is deliberately no fallback to a file, so an option that cannot be honoured is absent
        // rather than present and broken.
        Assert.False(Form(new UnavailableSecretStore()).ShowPasswordOption);
        Assert.True(Form(new RecordingSecrets()).ShowPasswordOption);
    }

    [Fact]
    public async Task A_password_goes_to_the_keychain_and_not_to_settings()
    {
        var secrets = new RecordingSecrets();
        var form = Form(secrets);
        form.RemoteUsePassword = true;
        form.RemotePassword = "hunter2";

        await form.AddRemoteCommand.ExecuteAsync(null);

        var stored = Assert.Single(Saved().RemoteEngines);
        Assert.True(stored.UsePassword);
        Assert.Equal("hunter2", secrets.Stored[SecretKeys.Engine(stored.Id)]);
        Assert.DoesNotContain("hunter2", await File.ReadAllTextAsync(_path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_keychain_that_refuses_saves_nothing_at_all()
    {
        // The failure mode this avoids: an engine in the switcher that looks configured and fails at
        // connect time, with ssh blamed for a password that was never stored.
        var form = Form(new RecordingSecrets(accepts: false));
        form.RemoteUsePassword = true;
        form.RemotePassword = "hunter2";

        await form.AddRemoteCommand.ExecuteAsync(null);

        Assert.Empty(Saved().RemoteEngines);
        Assert.NotNull(form.RemoteError);
    }

    [Fact]
    public void A_password_and_a_key_are_alternatives_not_a_pair()
    {
        // ssh would try the key and then ask for the password anyway, so a form holding both means
        // whichever was set last quietly wins.
        var form = Form(new RecordingSecrets());
        form.RemoteKeyFile = _keyFile;
        form.RemoteUsePassword = true;

        Assert.Equal(string.Empty, form.RemoteKeyFile);
        Assert.True(form.CanAddRemote);
    }

    [Fact]
    public void Editing_an_engine_does_not_read_its_password_back_into_the_form()
    {
        // Nothing puts a stored secret back on screen. Leaving the box empty keeps what the keychain
        // has; typing replaces it.
        var secrets = new RecordingSecrets();
        var form = Form(secrets);
        form.RemoteUsePassword = true;
        form.RemotePassword = "hunter2";
        form.AddRemoteCommand.Execute(null);

        form.EditRemoteCommand.Execute(form.RemoteEngines[0]);

        Assert.True(form.RemoteUsePassword);
        Assert.Equal(string.Empty, form.RemotePassword);
    }
}
