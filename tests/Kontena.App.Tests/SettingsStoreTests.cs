using Kontena.App.Services;
using Kontena.Core.Models;
using Xunit;

namespace Kontena.App.Tests;

/// <summary>
/// Settings are one file with several owners. These cover the failure that costs data: a writer holding an
/// older copy quietly reverting what another writer stored.
/// </summary>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"kontena-settings-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private SettingsStore Store() => new(_path);

    [Fact]
    public void Update_keeps_what_another_writer_stored_after_this_copy_was_taken()
    {
        // Exactly what happened in the app: the shell reads settings at startup, the Settings page adds
        // remote engines, and then the shell records the backend you just switched to. Writing the shell's
        // whole copy took the remotes back out — and switching to a remote is the very thing that triggers
        // that write, so adding one and using it was enough to lose it.
        var store = Store();
        store.Save(new KontenaSettings());

        var stale = store.Load();                      // the shell's copy, taken before the remote existed

        store.Update(s => s with
        {
            RemoteEngines = [new RemoteEngine("r1", "Build server", RemoteEngineTransport.Ssh, "build-01")],
        });

        // The shell records the backend from that older copy. Saving it outright is what wiped the list;
        // rebasing on the file means only LastBackend moves.
        Assert.Empty(stale.RemoteEngines);
        store.Update(s => s with { LastBackend = "docker-remote:r1" });

        var reloaded = store.Load();
        Assert.Equal("docker-remote:r1", reloaded.LastBackend);
        Assert.Single(reloaded.RemoteEngines);
        Assert.Equal("build-01", reloaded.RemoteEngines[0].Host);
    }

    [Fact]
    public void Update_returns_the_settings_it_wrote()
    {
        // Callers keep the result as their own copy, so it has to be the merged state rather than the
        // change alone — otherwise the next read-modify-write starts from a hole.
        var store = Store();
        store.Save(new KontenaSettings { PinnedBackend = "docker" });

        var updated = store.Update(s => s with { LaunchAtLogin = true });

        Assert.True(updated.LaunchAtLogin);
        Assert.Equal("docker", updated.PinnedBackend);
    }

    [Fact]
    public void Update_starts_from_defaults_when_there_is_no_file_yet()
    {
        var updated = Store().Update(s => s with { Onboarded = true });

        Assert.True(updated.Onboarded);
        Assert.Equal(ThemePreference.Dark, updated.Theme);
    }

    [Fact]
    public void Save_still_replaces_the_file_wholesale()
    {
        // Documented behaviour, not an oversight: the screenshot harness writes a settings file it fully
        // controls. Anything with other owners must go through Update.
        var store = Store();
        store.Save(new KontenaSettings { PinnedBackend = "docker" });
        store.Save(new KontenaSettings { LaunchAtLogin = true });

        var reloaded = store.Load();
        Assert.Null(reloaded.PinnedBackend);
        Assert.True(reloaded.LaunchAtLogin);
    }
}
