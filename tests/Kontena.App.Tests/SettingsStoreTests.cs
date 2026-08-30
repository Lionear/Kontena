using System.Runtime.Versioning;
using Kontena.App.Services;
using Kontena.Sdk.Models;
using Xunit;
using Kontena.Core.Models;

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
        foreach (var path in new[] { _path, _path + ".corrupt", _path + ".tmp" })
            if (File.Exists(path))
                File.Delete(path);
    }

    private SettingsStore Store() => new(_path);

    // The analyzer cannot see that Skip.If already keeps this off Windows, so it is told here.
    [SkippableFact, UnsupportedOSPlatform("windows")]
    public void A_saved_file_is_readable_only_by_its_owner()
    {
        // No secret is in here, but the hosts, usernames and kubeconfig paths are worth nothing to
        // another account on this machine — and 0644 is what the usual umask gives.
        Skip.If(OperatingSystem.IsWindows(), "Unix file modes only.");

        Store().Save(new KontenaSettings());

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_path));
    }

    [SkippableFact, UnsupportedOSPlatform("windows")]
    public void A_file_left_wide_open_by_an_older_version_is_narrowed_on_the_next_save()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix file modes only.");

        File.WriteAllText(_path, "{}");
        File.SetUnixFileMode(
            _path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        Store().Save(new KontenaSettings());

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_path));
    }

    [SkippableFact, UnsupportedOSPlatform("windows")]
    public void A_directory_this_write_did_not_create_is_left_alone()
    {
        // The path is not always the app's own config directory, and narrowing someone else's is not
        // this method's call — the temp directory every other test writes into is the obvious case.
        Skip.If(OperatingSystem.IsWindows(), "Unix file modes only.");

        var before = File.GetUnixFileMode(Path.GetDirectoryName(_path)!);

        Store().Save(new KontenaSettings());

        Assert.Equal(before, File.GetUnixFileMode(Path.GetDirectoryName(_path)!));
    }

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
    public void A_file_that_cannot_be_read_is_kept_before_the_next_write_replaces_it()
    {
        // KON-432. A power cut, a full disk or a hand-merged file leaves settings.json unparseable. The
        // app starts on defaults, and the first preference the user changes after that used to write
        // those defaults straight over the file — taking the remote engines, registries and kubeconfig
        // paths with it, silently and for good. It may still overwrite; it may not be the only copy.
        const string corrupt = """{ "RemoteEngines": [ { "Id": "r1", "Host": "build-0""";
        File.WriteAllText(_path, corrupt);

        var store = Store();
        store.Update(s => s with { LaunchAtLogin = true });

        Assert.NotNull(store.LastLoadError);
        Assert.Equal(corrupt, File.ReadAllText(store.QuarantinePath));
        Assert.True(store.Load().LaunchAtLogin);
        Assert.Null(store.LastLoadError);
    }

    [Fact]
    public void A_save_leaves_no_half_written_file_behind()
    {
        // The write goes to a temporary file and is renamed over the settings, so that a crash mid-write
        // cannot leave a truncated one. The rename has to be the last step: a leftover .tmp means the
        // file a reader finds is not the one this method wrote.
        var store = Store();
        store.Save(new KontenaSettings { PinnedBackend = "docker" });

        Assert.False(File.Exists(_path + ".tmp"));
        Assert.Equal("docker", store.Load().PinnedBackend);
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
