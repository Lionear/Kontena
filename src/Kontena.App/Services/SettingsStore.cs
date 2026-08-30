using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kontena.Sdk;
using Kontena.Sdk.Models;
using Kontena.Core.Models;

namespace Kontena.App.Services;

/// <summary>Loads and saves <see cref="KontenaSettings"/> as JSON under the user's config dir.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public SettingsStore()
    {
        _path = Path.Combine(ProductInfo.DataDirectory, "settings.json");
    }

    /// <summary>A store over a specific file. For tests, which must not touch the real settings.</summary>
    internal SettingsStore(string path) => _path = path;

    /// <summary>Where <see cref="Load"/> puts a copy of a file it could not read.</summary>
    public string QuarantinePath => _path + ".corrupt";

    /// <summary>
    /// Why the last <see cref="Load"/> could not read the file, or <c>null</c> if it read fine (KON-432).
    /// Whoever asks can say so; nothing here does, because the load that matters happens before there is
    /// a window to say it in.
    /// </summary>
    public string? LastLoadError { get; private set; }

    public KontenaSettings Load()
    {
        LastLoadError = null;

        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<KontenaSettings>(json, Options)
                    ?? throw new JsonException("The settings file holds no object.");
            }
        }
        catch (Exception e)
        {
            // Defaults rather than crash, as before — but not silently (KON-432). Falling back means the
            // next preference the user changes writes a whole file of defaults over this one, and their
            // remotes, registries and kubeconfig paths are gone for good. A power cut mid-write or a
            // hand-merged file is enough to get here, and such a file is usually still readable by eye,
            // so the bytes are kept next to the settings before anything replaces them.
            LastLoadError = e.Message;
            Diag.Mark($"settings unreadable ({e.GetType().Name}); kept a copy at {QuarantinePath}");
            Quarantine();
        }

        return new KontenaSettings();
    }

    /// <summary>
    /// Copies the unreadable file aside. A copy rather than a move: a read that failed because something
    /// else held the file open is transient, and taking the settings away over it would cause the very
    /// loss this exists to prevent. Best-effort — a machine that cannot write this still has to start.
    /// </summary>
    private void Quarantine()
    {
        try
        {
            File.Copy(_path, QuarantinePath, overwrite: true);
            RestrictToOwner(QuarantinePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception)
        {
            // Nothing to do about it here, and less than nothing to gain from failing the load.
        }
    }

    /// <summary>
    /// Changes settings on top of what is on disk right now, and returns the result.
    /// <para>
    /// The only supported way to write. Settings are one file but several owners — the shell remembers the
    /// open backend and window geometry, the Settings page owns preferences, registries and remotes — and
    /// each holds its own copy. Saving such a copy writes every field, so it silently reverts whatever
    /// another owner changed after that copy was taken. That is how a list of configured remote engines
    /// disappears on the next backend switch. Re-reading first means a writer can only affect the fields it
    /// actually touches.
    /// </para>
    /// </summary>
    public KontenaSettings Update(Func<KontenaSettings, KontenaSettings> change)
    {
        var updated = change(Load());
        Save(updated);
        return updated;
    }

    /// <summary>
    /// Writes settings as given, replacing the file. Prefer <see cref="Update"/>: this overwrites fields
    /// the caller may not know about. Public for the first write of a fresh object and for tools.
    /// </summary>
    public void Save(KontenaSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path)!;

            // Only a directory this write created gets its mode set: the path can be pointed elsewhere
            // (tests, tools), and narrowing a directory somebody else owns is not this method's call.
            var created = !Directory.Exists(directory);
            Directory.CreateDirectory(directory);
            if (created)
                RestrictToOwner(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            // Written beside the settings and renamed over them, so the file a reader finds is either
            // the old one or the new one and never half of either (KON-432). Writing in place meant a
            // crash, a full disk or a killed process could leave a truncated file — which the next
            // start cannot parse, and which used to cost the whole configuration. One fixed name rather
            // than a unique one per write: every writer in the app is on the UI thread, so two saves
            // cannot be in here at once. A second process would need a name of its own.
            var pending = _path + ".tmp";

            // The mode is set before the content exists, so the fields below are never briefly
            // world-readable — and the rename carries it, so a file written by an older version is
            // narrowed on its next save.
            File.Create(pending).Dispose();
            RestrictToOwner(pending, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            using (var stream = new FileStream(pending, FileMode.Truncate, FileAccess.Write))
            {
                JsonSerializer.Serialize(stream, settings, Options);

                // Flushed to the platter, not just to the page cache: without this the rename can
                // reach disk before the bytes do, which is a zero-length settings file after a power
                // cut — the failure this whole path is here to rule out.
                stream.Flush(flushToDisk: true);
            }

            File.Move(pending, _path, overwrite: true);
        }
        catch
        {
            // Best-effort persistence; a failed write must not take the app down.
        }
    }

    /// <summary>
    /// Keeps <paramref name="path"/> to its owner on Unix (KON-187). No secret is in here — those live
    /// in the keychain (KON-52) — but remote engine hosts and users, registry usernames and the
    /// kubeconfig paths Kontena reads are reconnaissance for anyone else with an account on the machine.
    /// Windows inherits the user profile's ACL and needs nothing.
    /// </summary>
    private static void RestrictToOwner(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(path, mode);
        }
        catch (Exception)
        {
            // A filesystem that cannot express this (a mounted share, a container volume) is not a
            // reason to lose the settings.
        }
    }
}
